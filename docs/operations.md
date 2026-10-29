# Operations

How to run Lookout as a service: configuration, releases, backups, monitoring, and what happens when a dependency fails. The backup and restore procedure below was run against a live stack on 2026-10-29 and verified.

## Configuration

| Setting | Secret | Purpose |
|---|---|---|
| `ConnectionStrings:Lookout` | yes | Runtime connection. Use a login in the `lookout_app` role: it can insert and read the audit trail, never change it. |
| `ConnectionStrings:LookoutMigrations` | yes | Owner connection, used only to apply migrations. Leave unset in production if migrations run as a separate step. |
| `Lookout:MigrateOnStartup` | no | `true` in docker compose. For production, prefer `false` and a reviewed migration step. |
| `Lookout:Jwt:Key` | yes | Token signing key, at least 32 characters. Replaced by an OIDC issuer in a real deployment. |
| `Lookout:DevAuth` | no | Development sign-in. Never on for a reachable deployment; the API logs a warning if it is on outside Development. |
| `Lookout:Mqtt:Host`, `Username`, `Password` | password | Broker. Without a host the engine runs in loopback mode and nothing leaves the server. |
| `Lookout:WebPush:PublicKey`, `PrivateKey` | private key | VAPID identity for push alerts. Generate with `lookout-sim vapid`. |
| `Lookout:Cot:Host`, `Port` | no | Optional TAK server for Cursor on Target export. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | Optional OTLP collector for traces and metrics. |

## Releasing

1. CI must be green: engine and API tests (the API tests run every migration on a real TimescaleDB, as the restricted runtime role), the dashboard build, and the image builds.
2. Back up the database (below).
3. Review the SQL a release will run:

   ```bash
   dotnet ef migrations script --idempotent --project src/Lookout.Api -o release.sql
   ```

4. Apply it as the owner, then deploy the API with `Lookout:MigrateOnStartup=false`, connecting as the runtime role.
5. Roll forward, not back. Each migration has a `Down`, but undoing a migration on live data loses whatever was written since. Restore the backup if a release must be abandoned.

New tables need grants for `lookout_app` in the same migration. The API tests catch a missing grant, because they run as that role.

## Assigning crew devices

A crew's link device reports only after the incident commander assigns it. The device id is the modem IMEI or another stable id of 3 to 32 letters, digits or dashes. The device logs in to the broker with that id as its username.

With development sign-in on, an incident commander token comes from the API:

```bash
IC_TOKEN=$(curl -s -X POST -H 'Content-Type: application/json' -d '{"role":"ic","name":"ops"}' http://localhost:8080/api/dev/token | jq -r .token)
```

```bash
curl -X PUT -d "" -H "Authorization: Bearer $IC_TOKEN" http://localhost:8080/api/incidents/$INCIDENT/crews/ALPHA/devices/300434060012345
```

Assigning a device that already belongs to another crew or incident moves it. `DELETE` on the same path unassigns it. Both are recorded in the audit trail. Reports from an unassigned device are dropped and logged once per device. The field simulator assigns one device per crew, named `SIM-{incident}-{crew}`.

## Backup and restore

TimescaleDB needs its pre- and post-restore steps around `pg_restore`, and the roles must exist before the restore so grants come back.

**Back up** (custom format, compressed):

```bash
docker exec lookout-db-1 pg_dump -U lookout -d lookout -Fc -f /tmp/lookout.dump
docker cp lookout-db-1:/tmp/lookout.dump ./lookout.dump
```

`pg_dump` warns about circular foreign keys in TimescaleDB's own catalog; the procedure below handles them.

**Restore** into a fresh database whose init script has created `lookout_app` and `lookout_runtime` (`deploy/postgres/020_lookout_runtime.sql`):

```bash
docker cp ./lookout.dump <container>:/tmp/lookout.dump
docker exec <container> psql -U lookout -d lookout -c "SELECT timescaledb_pre_restore();"
docker exec <container> pg_restore -U lookout -d lookout --no-owner --role=lookout /tmp/lookout.dump
docker exec <container> psql -U lookout -d lookout -c "SELECT timescaledb_post_restore();"
```

Verified on 2026-10-29 with a backup of the running stack restored into a new container:

| Check | Result |
|---|---|
| Row counts (audit events, alerts, crew fixes, incidents) | identical: 213, 38, 214, 5 |
| Hypertables | `audit_events`, `crew_fixes` |
| Append-only triggers | present; `DELETE FROM audit_events` refused |
| Runtime role grants on the audit trail | `INSERT`, `SELECT` only |
| Compression and retention jobs | all three present |
| EF migration history | complete; the API applies nothing on start |

Practise a restore regularly. A backup that has never been restored is a hope, not a backup.

## Retention

| Data | Policy |
|---|---|
| `crew_fixes` | compressed after 7 days, dropped after 90 |
| `audit_events` | compressed after 30 days, never dropped |
| Everything else | kept |

Purging audit history is deliberately not possible through the API or the runtime role. If a records policy ever requires it, it is a reviewed migration run by the owner.

## What to watch

Metrics come from OpenTelemetry (see `docker-compose.observability.yml`). Suggested alerts, as starting points to tune:

| Signal | Alert when | Why |
|---|---|---|
| `lookout.engine.tick.duration` p99 | above 500 ms for 5 minutes | The loop is falling behind its 1-second tick; alerts will be late. |
| `lookout.link.downlink.failures` | any, for 2 minutes | The broker is unreachable; warnings are queued, not delivered. |
| `lookout.link.uplink.messages` | zero for 5 minutes while an incident runs | No crew fixes are arriving: link or field problem. |
| `lookout.alerts.escalated` | any | A crew did not acknowledge a warning; the IC must act. This is an operational alert, not just a technical one. |
| `lookout.alerts.ack.latency` p90 | above 2 minutes | Warnings are reaching crews slowly or being missed. |
| `/api/health` | failing | API down. |
| Runner errors in the log (`Live runner: ... failed; continuing`) | any burst | One incident, message or export is failing repeatedly. |

## When a dependency fails

| Failure | What Lookout does |
|---|---|
| Broker down | Downlinks are re-queued every tick; the MQTT client reconnects with backoff up to 30 s; crew fixes stop, so comms-lost alerts follow after 5 and 15 minutes. |
| Database down | Each tick's work fails and is logged; an alert that could not be saved is rolled back in the crew's latch and raised again once the database returns. |
| API restart | Incidents reload from the database, with each crew's last fix and last alert level, so a restart sends no false warnings. Open warnings are re-offered to the link. |
| API container recreated | nginx re-resolves the API every 10 s, so the dashboard recovers on its own. |
| NWS slow or down | Fetches run off the loop; the engine keeps the last wind it had. |
| TAK server unreachable | Export gives up after 3 s and retries every 30 s; alerting is unaffected. |
| Push service unreachable | Logged; the crew link is unaffected. Gone subscriptions are deleted. |
| A scenario file that cannot be loaded | That incident is skipped with an error; every other incident keeps running. |

## Rotating secrets

| Secret | Effect of rotating |
|---|---|
| JWT signing key | Every issued token stops working; users sign in again. |
| VAPID keys | Every push subscription stops working; crews tap "Enable push alerts" again. |
| MQTT passwords | Update `deploy/mosquitto/passwd` with `mosquitto_passwd`, restart the broker, then the clients. |
| Database passwords | Change the role password, update the connection string, restart the API. |
