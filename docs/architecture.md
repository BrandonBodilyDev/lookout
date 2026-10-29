# Design decisions

Short records of the choices that shape Lookout, why they were made, and what they cost. Each one is small enough to revisit.

## 1. The engine has no web or database dependencies

**Decision.** `Lookout.Engine` holds the terrain grid, spread models, travel rates, rules, alert latch, alert state machine and scenario loader. It references NetTopologySuite and YamlDotNet and nothing else.

**Why.** The engine is where correctness matters most, so it should be testable in milliseconds with no containers. The replay simulator, the live runner, and the tests all call the same code.

**Cost.** A few types are mirrored at the edges. The API maps engine results to JSON shapes, and the database stores alert states as strings.

## 2. One crew monitor for replay and live

**Decision.** `CrewMonitor` runs the three rules and the hysteresis latch for one crew. The replay simulator and the live runner both use it.

**Why.** A replay and a live run of the same scenario produce the same alerts. The demo's acceptance test therefore also checks the live alert logic.

**Cost.** The monitor holds a little state (latch, last progress), so live and replay must feed it in time order.

## 3. Grid minimum travel time, reverse search

**Decision.** Fire travel time uses Dijkstra over a 30 m grid with 16 neighbours, as in FlamMap MTT. For alerts, one reverse search runs from the crew's remaining route, seeded with minus the crew's arrival time at each route point.

**Why.** A single search gives the margin from wherever the front is, and also the leave-now zone to draw. The search stops once margins exceed anything that could change the alert level. The whole 45-minute demo replay, 91 ticks for two crews plus the true fire, runs in about 4 seconds on a laptop, process start included.

**Cost.** Grid direction bias is a few percent, which a test bounds against the closed-form ellipse. Spread inside one weather phase uses constant conditions.

## 4. Hysteresis before the link

**Decision.** Escalations go out at once. Downgrades go out only after the lower level holds for 2 minutes.

**Why.** The first terrain run showed a margin sitting on a threshold, flapping WARNING to CRITICAL to WARNING within a minute. Over a satellite link, each flap costs a session and the crew's trust.

**Cost.** An all-clear arrives up to 2 minutes late. That is the safe direction to be late in.

## 5. Fixed binary messages on the crew link

**Decision.** Positions are 16 bytes, acknowledgements 8, alerts at most 270, in fixed big-endian layouts. There is no CBOR or protobuf on the crew link.

**Why.** Iridium SBD allows 340 bytes up and 270 down, and bills in small increments. A fixed layout is the smallest encoding and needs no schema negotiation on a device. The crew is identified by the topic, as by the modem IMEI in SBD, not repeated in every message.

**Cost.** Changing a layout needs a type byte or a version, and devices must be updated together.

## 6. The governor runs on scenario time and reports delivery

**Decision.** `LinkGovernor` keeps one outbound slot per crew, spaces transmissions 40 seconds apart per device, sends the most severe first, and repeats unacknowledged warnings every 60 seconds. The caller reports each transmission as delivered or failed.

**Why.** A newer alert should replace a stale one, not queue behind it. Delivery reporting came from a real failure: when the broker dropped a connection, an informational message had already been counted as sent and was lost. Scenario time keeps accelerated demos honest about link behaviour.

**Cost.** One slot means an older unacknowledged warning is superseded, not repeated, once a newer one exists. The state machine records that explicitly.

## 7. One loop owns live state

**Decision.** The live runner is a single loop. MQTT messages and HTTP signals go into channels, and the loop drains them before each tick.

**Why.** Per-incident state (front, wind, fixes, monitors, governor) needs no locks, and ordering is obvious when reading the code.

**Cost.** One slow tick delays every incident. At the demo's scale this is far from a problem. Many simultaneous incidents would call for one loop per incident.

## 8. Optimistic concurrency with PostgreSQL's xmin

**Decision.** Alerts, routes and safety zones use `xmin` as a row version. Every change carries the version the client saw. A mismatch returns 409 with the current state.

**Why.** An incident commander and a crew device can act on the same alert at the same moment. Exactly one of them should win, and the other should see what happened instead of overwriting it. `xmin` needs no extra column and changes on every update.

**Cost.** Clients must round-trip the version. Devices acknowledging by link sequence use the current version, because a modem cannot know it. A duplicate or late device acknowledgement is recorded and ignored.

## 9. The database enforces an append-only audit trail

**Decision.** Every alert change and its audit event are written in one transaction. A trigger on `audit_events` rejects UPDATE, DELETE and TRUNCATE. The table is a TimescaleDB hypertable, compressed after 30 days and never dropped.

**Why.** An audit trail the application promises not to edit is weaker than one the database refuses to edit. That matters for after-action reviews, and for any regulated workflow.

**Cost.** Corrections are new events. Purging data requires a deliberate migration, not an API call.

## 10. Crew positions over HTTPS use the satellite format

**Decision.** A phone posting its GPS over HTTPS has its position packed into the same 16-byte report as a tracker and handed to the same uplink handler.

**Why.** There is one code path for position handling, whatever the transport.

**Cost.** The phone's extra precision and accuracy fields are dropped. Accuracy could go in a spare byte if it proves useful.

## 11. TAK export only on the server

**Decision.** Cursor on Target events are built from server state and sent over TCP to a TAK server. They are never sent on the crew link.

**Why.** A minimal CoT position is about 400 bytes of XML, too large for the 270-byte downlink. Gateways that translate a compact field format into CoT are the established pattern, as Meshtastic does for LoRa.

**Cost.** TAK users see crews as the server knows them, which is as current as the last fix.

## 12. A synthetic camera to test the drone pipeline

**Decision.** The drone pipeline is tested by rendering the scenario's true fire through a virtual camera, then detecting, projecting and mapping it, and scoring the result against the truth.

**Why.** Real fire footage is large, licensed per dataset, and has no ground truth on the scenario's terrain. The synthetic test proves the geometry and plumbing: frames, poses, projection, terrain intersection and mapping. It also caught a real flaw: the first color rule accepted dry grass.

**Cost.** Synthetic images are easy. Passing these tests says nothing about smoke, glare or shadows. Real footage remains the next step.

## 13. Development sign-in, production identity later

**Decision.** The API issues short-lived JWTs for a chosen role when `Lookout:DevAuth` is on. Roles and policies are real; only the issuer is a stand-in.

**Why.** It makes the role model testable and demonstrable without an identity provider. Swapping in Entra ID or another OIDC issuer changes configuration, not authorization code.

**Cost.** Dev auth must never be enabled on a reachable deployment. Startup refuses to run live mode without a signing key of at least 32 characters.
