using Lookout.Api.Auth;
using Lookout.Api.Data;
using Lookout.Api.Weather;
using Lookout.Engine.Workflow;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Lookout.Api.Live;

public static class LiveEndpoints
{
    public sealed record TokenRequest(string Role, string? Name, string? CrewId);
    public sealed record CreateIncident(string ScenarioId, double ClockRate = 1, string? WindSource = null);
    public sealed record VersionedAction(uint Version, string? Reason);
    public sealed record PutRoute(string Name, string SafetyZone, double[][] Path, uint? Version);
    public sealed record PutZone(string Name, double[][] Ring, uint? Version);

    public static void MapLive(this WebApplication app, bool devAuth)
    {
        if (devAuth)
        {
            // Development sign-in: pick a role, get a token. Production would use Entra ID / OIDC instead.
            app.MapPost("/api/dev/token", (TokenRequest r, DevTokenIssuer issuer) =>
                !Roles.All.Contains(r.Role) ? Results.BadRequest(new { error = $"role must be one of {string.Join(", ", Roles.All)}" })
                : r.Role == Roles.Crew && string.IsNullOrWhiteSpace(r.CrewId) ? Results.BadRequest(new { error = "crew tokens need a crewId" })
                : Results.Ok(new { token = issuer.Issue(r.Name ?? $"{r.Role}-{r.CrewId ?? "user"}".ToLowerInvariant(), r.Role, r.CrewId?.ToUpperInvariant()) }));
        }

        var api = app.MapGroup("/api/incidents").AddEndpointFilter<ProblemFilter>();

        api.MapPost("/", async (CreateIncident r, IncidentService svc, IncidentSignals signals, HttpContext http, CancellationToken ct) =>
        {
            var wind = string.Equals(r.WindSource, "nws", StringComparison.OrdinalIgnoreCase) ? WindSource.Nws : WindSource.Field;
            var incident = await svc.CreateAsync(r.ScenarioId, r.ClockRate, Actor.From(http.User), wind, ct);
            signals.Post(new IncidentCreated(incident.Id));
            return Results.Created($"/api/incidents/{incident.Id}", new { incident.Id, incident.Name, incident.StartedAt, incident.ClockRate });
        }).RequireAuthorization(Policies.Command);

        api.MapGet("/", async (LookoutDb db, CancellationToken ct) =>
            await db.Incidents.AsNoTracking().OrderByDescending(i => i.CreatedAt)
                .Select(i => new { i.Id, i.ScenarioId, i.Name, i.StartedAt, i.ClockRate, Status = i.Status.ToString().ToLower(), WindSource = i.WindSource.ToString().ToLower() })
                .ToListAsync(ct))
            .RequireAuthorization(Policies.Read);

        api.MapGet("/{id:guid}", async (Guid id, LookoutDb db, CancellationToken ct) =>
        {
            var i = await db.Incidents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException($"Incident {id}");
            var crews = await db.Crews.AsNoTracking().Where(c => c.IncidentId == id).OrderBy(c => c.CrewId).ToListAsync(ct);
            var routes = await db.EscapeRoutes.AsNoTracking().Where(r => r.IncidentId == id).OrderBy(r => r.RouteId).ToListAsync(ct);
            var zones = await db.SafetyZones.AsNoTracking().Where(z => z.IncidentId == id).OrderBy(z => z.ZoneId).ToListAsync(ct);
            return Results.Ok(new
            {
                i.Id, i.ScenarioId, i.Name, i.StartedAt, i.ClockRate, Status = i.Status.ToString().ToLowerInvariant(),
                WindSource = i.WindSource.ToString().ToLowerInvariant(),
                Crews = crews.Select(c => new { Id = c.CrewId, c.Name, Route = c.RouteId, c.Size, c.Pace }),
                Routes = routes.Select(RouteDto.From),
                SafetyZones = zones.Select(ZoneDto.From),
            });
        }).RequireAuthorization(Policies.Read);

        api.MapGet("/{id:guid}/alerts", async (Guid id, bool? open, LookoutDb db, CancellationToken ct) =>
        {
            var q = db.Alerts.AsNoTracking().Where(a => a.IncidentId == id);
            if (open == true) q = q.Where(a => a.State == AlertState.Queued || a.State == AlertState.Sent || a.State == AlertState.Escalated);
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
