using Lookout.Api.Auth;
using Lookout.Api.Data;
using Lookout.Api.Weather;
using Lookout.Engine.Workflow;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lookout.Api.Live;

public static class LiveEndpoints
{
    public sealed record TokenRequest(string Role, string? Name, string? CrewId, Guid? IncidentId);
    public sealed record CreateIncident(string ScenarioId, double ClockRate = 1, string? WindSource = null);
    public sealed record VersionedAction(uint Version, string? Reason);
    public sealed record PutRoute(string Name, string SafetyZone, double[][] Path, uint? Version);
    public sealed record PutZone(string Name, double[][] Ring, uint? Version);
    public sealed record PhonePosition(double Lat, double Lon, int? Battery, ushort? Sequence);
    public sealed record PushKeys(string P256dh, string Auth);
    public sealed record PushSubscribe(string Endpoint, PushKeys? Keys);

    /// <summary>
    /// The browser push services. The server will POST to a subscription's endpoint, so anything else
    /// would let a token holder make the server call arbitrary hosts (server-side request forgery).
    /// </summary>
    private static bool IsKnownPushService(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && (
            u.Host is "fcm.googleapis.com" or "updates.push.services.mozilla.com" or "web.push.apple.com"
            || u.Host.EndsWith(".push.apple.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith(".notify.windows.com", StringComparison.OrdinalIgnoreCase));

    public static void MapLive(this WebApplication app, bool devAuth)
    {
        if (devAuth)
        {
            // Development sign-in: pick a role, get a token. Production would use Entra ID / OIDC instead.
            app.MapPost("/api/dev/token", (TokenRequest r, DevTokenIssuer issuer) =>
                !Roles.All.Contains(r.Role) ? Results.BadRequest(new { error = $"role must be one of {string.Join(", ", Roles.All)}" })
                : r.Role == Roles.Crew && (string.IsNullOrWhiteSpace(r.CrewId) || r.IncidentId is null)
                    ? Results.BadRequest(new { error = "crew tokens need a crewId and an incidentId" })
                : Results.Ok(new
                {
                    token = issuer.Issue(r.Name ?? $"{r.Role}-{r.CrewId ?? "user"}".ToLowerInvariant(), r.Role,
                        r.Role == Roles.Crew ? r.CrewId!.ToUpperInvariant() : null, r.Role == Roles.Crew ? r.IncidentId : null),
                }));
        }

        var api = app.MapGroup("/api/incidents").AddEndpointFilter<ProblemFilter>().AddEndpointFilter<CrewIncidentScope>();

        api.MapPost("/", async (CreateIncident r, IncidentService svc, IncidentSignals signals, HttpContext http, CancellationToken ct) =>
        {
            var wind = string.Equals(r.WindSource, "nws", StringComparison.OrdinalIgnoreCase) ? WindSource.Nws : WindSource.Field;
            var incident = await svc.CreateAsync(r.ScenarioId, r.ClockRate, Actor.From(http.User), wind, ct);
            signals.Post(new IncidentCreated(incident.Id));
            return Results.Created($"/api/incidents/{incident.Id}", new { incident.Id, incident.Name, incident.StartedAt, incident.ClockRate });
        }).RequireAuthorization(Policies.Command);

        api.MapGet("/", async (LookoutDb db, HttpContext http, CancellationToken ct) =>
        {
            var actor = Actor.From(http.User);
            var q = db.Incidents.AsNoTracking();
            if (actor.Role == Roles.Crew) q = q.Where(i => i.Id == actor.IncidentId);
            return await q.OrderByDescending(i => i.CreatedAt)
                .Select(i => new { i.Id, i.ScenarioId, i.Name, i.StartedAt, i.ClockRate, Status = i.Status.ToString().ToLower(), WindSource = i.WindSource.ToString().ToLower() })
                .ToListAsync(ct);
        }).RequireAuthorization(Policies.Read);

        api.MapGet("/{id:guid}", async (Guid id, LookoutDb db, CancellationToken ct) =>
        {
            var i = await db.Incidents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException($"Incident {id}");
            var crews = await db.Crews.AsNoTracking().Where(c => c.IncidentId == id).OrderBy(c => c.CrewId).ToListAsync(ct);
            var routes = await db.EscapeRoutes.AsNoTracking().Where(r => r.IncidentId == id).OrderBy(r => r.RouteId).ToListAsync(ct);
            var zones = await db.SafetyZones.AsNoTracking().Where(z => z.IncidentId == id).OrderBy(z => z.ZoneId).ToListAsync(ct);
            var devices = await db.Devices.AsNoTracking().Where(d => d.IncidentId == id).ToListAsync(ct);
            return Results.Ok(new
            {
                i.Id, i.ScenarioId, i.Name, i.StartedAt, i.ClockRate, Status = i.Status.ToString().ToLowerInvariant(),
                WindSource = i.WindSource.ToString().ToLowerInvariant(),
                Crews = crews.Select(c => new
                {
                    Id = c.CrewId, c.Name, Route = c.RouteId, Routes = new[] { c.RouteId }.Concat(c.AlternateRouteIds), c.Size, c.Pace,
                    Devices = devices.Where(d => d.CrewId == c.CrewId).Select(d => d.DeviceId).Order(),
                }),
                Routes = routes.Select(RouteDto.From),
                SafetyZones = zones.Select(ZoneDto.From),
            });
        }).RequireAuthorization(Policies.Read);

        api.MapGet("/{id:guid}/alerts", async (Guid id, bool? open, LookoutDb db, CancellationToken ct) =>
        {
            var q = db.Alerts.AsNoTracking().Where(a => a.IncidentId == id);
            // Open: still needs something. Informational messages (no ack required) are done once sent.
            if (open == true) q = q.Where(a => a.State == AlertState.Queued || a.State == AlertState.Escalated || (a.State == AlertState.Sent && a.RequiresAck));
            return (await q.OrderByDescending(a => a.RaisedAt).Take(200).ToListAsync(ct)).Select(AlertDto.From);
        }).RequireAuthorization(Policies.Read);

        api.MapPost("/{id:guid}/alerts/{alertId:guid}/ack", async (Guid id, Guid alertId, VersionedAction r, AlertService svc,
                IncidentSignals signals, IHubContext<IncidentHub> hub, HttpContext http, CancellationToken ct) =>
            await Closed(id, await svc.AcknowledgeAsync(id, alertId, r.Version, Actor.From(http.User), ct), signals, hub, ct))
            .RequireAuthorization(Policies.Field);

        api.MapPost("/{id:guid}/alerts/{alertId:guid}/cancel", async (Guid id, Guid alertId, VersionedAction r, AlertService svc,
                IncidentSignals signals, IHubContext<IncidentHub> hub, HttpContext http, CancellationToken ct) =>
            await Closed(id, await svc.CancelAsync(id, alertId, r.Version, r.Reason ?? "", Actor.From(http.User), ct), signals, hub, ct))
            .RequireAuthorization(Policies.Command);

        api.MapGet("/{id:guid}/frame", (Guid id, LiveSnapshots snapshots) =>
            snapshots.Get(id) is { } f ? Results.Ok(f) : Results.NoContent())
            .RequireAuthorization(Policies.Read);

        // Current Cursor on Target events, for inspection or import into a TAK client.
        api.MapGet("/{id:guid}/cot", (Guid id, LiveSnapshots snapshots) =>
            Results.Text(new System.Xml.Linq.XElement("events", snapshots.GetCot(id)).ToString(), "application/xml"))
            .RequireAuthorization(Policies.Read);

        // A crew phone without a satellite modem reports position over HTTPS. The API packs it into the
        // same 16-byte position report a crew device sends over the link and hands it to the live runner.
        api.MapPost("/{id:guid}/crews/{crewId}/position", (Guid id, string crewId, PhonePosition p, IncidentSignals signals, TimeProvider clock, HttpContext http) =>
        {
            var actor = Actor.From(http.User);
            if (actor.Role == Roles.Crew && !string.Equals(actor.CrewId, crewId, StringComparison.OrdinalIgnoreCase))
                return Results.Problem($"Crew {actor.CrewId} cannot report a position for crew {crewId}.", statusCode: StatusCodes.Status403Forbidden);
            if (p.Lat is < -90 or > 90 || p.Lon is < -180 or > 180)
                return Results.Problem("Latitude or longitude out of range.", statusCode: StatusCodes.Status400BadRequest);
            var payload = Link.WireFormat.EncodePosition(new Link.PositionReport(p.Sequence ?? 0, clock.GetUtcNow(), p.Lat, p.Lon, p.Battery ?? 100));
            signals.Post(new UplinkReceived(id, crewId.ToUpperInvariant(), payload));
            return Results.Accepted(value: new { bytes = payload.Length });
        }).RequireAuthorization(Policies.Field);

        // Web Push: a crew phone registers so warnings can wake it with the page closed.
        app.MapGet("/api/push/public-key", (Push.WebPushSettings push) =>
            push.Enabled ? Results.Ok(new { publicKey = push.PublicKey }) : Results.NotFound());

        api.MapPost("/{id:guid}/crews/{crewId}/push", async (Guid id, string crewId, PushSubscribe s, LookoutDb db, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            var actor = Actor.From(http.User);
            crewId = crewId.ToUpperInvariant();
            if (actor.Role == Roles.Crew && actor.CrewId != crewId)
                return Results.Problem($"Crew {actor.CrewId} cannot subscribe for crew {crewId}.", statusCode: StatusCodes.Status403Forbidden);
            // Push services are https; anything else (or junk) is refused rather than called later.
            if (s.Keys is not { P256dh.Length: > 0 and <= 128, Auth.Length: > 0 and <= 64 }
                || s.Endpoint is not { Length: <= 1024 } || !Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var endpoint) || !IsKnownPushService(endpoint))
                return Results.Problem("Not a valid push subscription from a browser push service.", statusCode: StatusCodes.Status400BadRequest);
            if (!await db.Crews.AnyAsync(c => c.IncidentId == id && c.CrewId == crewId, ct)) throw new NotFoundException($"Crew {crewId}");
            if (!await db.PushSubscriptions.AnyAsync(p => p.IncidentId == id && p.CrewId == crewId && p.Endpoint == s.Endpoint, ct))
            {
                db.PushSubscriptions.Add(new PushSubscriptionRecord
                {
                    IncidentId = id, CrewId = crewId, Endpoint = s.Endpoint, P256dh = s.Keys.P256dh, Auth = s.Keys.Auth, CreatedAt = clock.GetUtcNow(),
                });
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { /* the same phone subscribed twice at once: already registered */ }
            }
            return Results.NoContent();
        }).RequireAuthorization(Policies.Field);

        // Device registry: which link device (modem IMEI, or a simulator id) reports for which crew.
        api.MapPut("/{id:guid}/crews/{crewId}/devices/{deviceId}", async (Guid id, string crewId, string deviceId, LookoutDb db,
            IncidentSignals signals, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            if (!Link.Topics.IsValidDeviceId(deviceId))
                return Results.Problem("Device ids are 3 to 32 letters, digits or dashes.", statusCode: StatusCodes.Status400BadRequest);
            crewId = crewId.ToUpperInvariant();
            if (!await db.Crews.AnyAsync(c => c.IncidentId == id && c.CrewId == crewId, ct)) throw new NotFoundException($"Crew {crewId}");
            var actor = Actor.From(http.User);
            var now = clock.GetUtcNow();
            var device = await db.Devices.SingleOrDefaultAsync(d => d.DeviceId == deviceId, ct);
            var from = device is null ? null : new { device.IncidentId, device.CrewId };
            if (device is null) db.Devices.Add(device = new DeviceRecord { DeviceId = deviceId, IncidentId = id, CrewId = crewId, AssignedAt = now, AssignedBy = actor.Name });
            else { device.IncidentId = id; device.CrewId = crewId; device.AssignedAt = now; device.AssignedBy = actor.Name; }
            db.AuditEvents.Add(new AuditEvent
            {
                Time = now, IncidentId = id, CrewId = crewId, Type = "device.assigned", Actor = actor.Name, Role = actor.Role,
                Details = System.Text.Json.JsonSerializer.SerializeToDocument(new { deviceId, from }),
            });
            await db.SaveChangesAsync(ct);
            signals.Post(new DevicesChanged(id));
            return Results.Ok(new { deviceId, incidentId = id, crewId });
        }).RequireAuthorization(Policies.Command);

        api.MapDelete("/{id:guid}/crews/{crewId}/devices/{deviceId}", async (Guid id, string crewId, string deviceId, LookoutDb db,
            IncidentSignals signals, TimeProvider clock, HttpContext http, CancellationToken ct) =>
        {
            crewId = crewId.ToUpperInvariant();
            var device = await db.Devices.SingleOrDefaultAsync(d => d.DeviceId == deviceId && d.IncidentId == id && d.CrewId == crewId, ct)
                ?? throw new NotFoundException($"Device {deviceId} on crew {crewId}");
            var actor = Actor.From(http.User);
            db.Devices.Remove(device);
            db.AuditEvents.Add(new AuditEvent
            {
                Time = clock.GetUtcNow(), IncidentId = id, CrewId = crewId, Type = "device.unassigned", Actor = actor.Name, Role = actor.Role,
                Details = System.Text.Json.JsonSerializer.SerializeToDocument(new { deviceId }),
            });
            await db.SaveChangesAsync(ct);
            signals.Post(new DevicesChanged(id));
            return Results.NoContent();
        }).RequireAuthorization(Policies.Command);

        api.MapGet("/{id:guid}/fixes", async (Guid id, string crewId, int? limit, LookoutDb db, CancellationToken ct) =>
            await db.CrewFixes.AsNoTracking().Where(f => f.IncidentId == id && f.CrewId == crewId)
                .OrderByDescending(f => f.Time).Take(Math.Clamp(limit ?? 100, 1, 1000))
                .Select(f => new { f.Time, f.ScenarioMin, Lon = f.Position.X, Lat = f.Position.Y, f.Bytes })
                .ToListAsync(ct))
            .RequireAuthorization(Policies.Read);

        api.MapGet("/{id:guid}/audit", async (Guid id, int? limit, LookoutDb db, CancellationToken ct) =>
            await db.AuditEvents.AsNoTracking().Where(e => e.IncidentId == id)
                .OrderByDescending(e => e.Time).ThenByDescending(e => e.Id).Take(Math.Clamp(limit ?? 200, 1, 1000))
                .Select(e => new { e.Id, e.Time, e.Type, e.Actor, e.Role, e.CrewId, e.AlertId, e.Details })
                .ToListAsync(ct))
            .RequireAuthorization(policy => policy.RequireRole(Roles.IncidentCommander, Roles.Observer));

        api.MapGet("/{id:guid}/summary", (Guid id, LookoutDb db, CancellationToken ct) => AfterAction.SummaryAsync(db, id, ct))
            .RequireAuthorization(policy => policy.RequireRole(Roles.IncidentCommander, Roles.Observer));

        api.MapGet("/{id:guid}/audit.csv", async (Guid id, LookoutDb db, CancellationToken ct) =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(await AfterAction.AuditCsvAsync(db, id, ct)), "text/csv", $"lookout-audit-{id}.csv"))
            .RequireAuthorization(policy => policy.RequireRole(Roles.IncidentCommander, Roles.Observer));

        api.MapPut("/{id:guid}/routes/{routeId}", async (Guid id, string routeId, PutRoute r, IncidentService svc, IncidentSignals signals, HttpContext http, CancellationToken ct) =>
        {
            var route = RouteDto.From(await svc.PutRouteAsync(id, routeId, r.Name, r.SafetyZone, r.Path, r.Version, Actor.From(http.User), ct));
            signals.Post(new LayoutChanged(id));
            return route;
        }).RequireAuthorization(Policies.Command);

        api.MapPut("/{id:guid}/zones/{zoneId}", async (Guid id, string zoneId, PutZone r, IncidentService svc, IncidentSignals signals, HttpContext http, CancellationToken ct) =>
        {
            var zone = ZoneDto.From(await svc.PutZoneAsync(id, zoneId, r.Name, r.Ring, r.Version, Actor.From(http.User), ct));
            signals.Post(new LayoutChanged(id));
            return zone;
        }).RequireAuthorization(Policies.Command);

        app.MapGet("/api/weather", async (double lat, double lon, NwsClient nws, CancellationToken ct) =>
        {
            try { return Results.Ok(await nws.GetAsync(lat, lon, ct)); }
            catch (HttpRequestException e) { return Results.Problem($"National Weather Service unavailable: {e.Message}", statusCode: 502); }
        }).RequireAuthorization(Policies.Read);
    }

    /// <summary>After an ack or cancel: stop the link repeating it and tell dashboards.</summary>
    private static async Task<AlertDto> Closed(Guid incidentId, AlertRecord alert, IncidentSignals signals, IHubContext<IncidentHub> hub, CancellationToken ct)
    {
        signals.Post(new AlertClosed(incidentId, alert.CrewId, alert.Id));
        var dto = AlertDto.From(alert);
        await hub.Clients.Group(incidentId.ToString()).SendAsync("alert", dto, ct);
        return dto;
    }

    /// <summary>Refuses a crew token on any endpoint for an incident other than the one it was issued for.</summary>
    private sealed class CrewIncidentScope : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            if (http.Request.RouteValues.TryGetValue("id", out var raw) && Guid.TryParse(raw?.ToString(), out var id)
                && !Actor.From(http.User).MayAccessIncident(id))
                return Results.Problem("This crew token is for a different incident.", statusCode: StatusCodes.Status403Forbidden);
            return await next(context);
        }
    }

    /// <summary>Maps domain exceptions to RFC 9457 problem responses; conflicts include the current state.</summary>
    private sealed class ProblemFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (LookoutException e)
            {
                return Results.Problem(detail: e.Message, statusCode: e.Status,
                    extensions: e.Current is null ? null : new Dictionary<string, object?> { ["current"] = e.Current });
            }
        }
    }
}
