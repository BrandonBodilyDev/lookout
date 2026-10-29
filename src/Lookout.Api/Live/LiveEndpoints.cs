using Lookout.Api.Auth;
using Lookout.Api.Data;
using Lookout.Engine.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Lookout.Api.Live;

public static class LiveEndpoints
{
    public sealed record TokenRequest(string Role, string? Name, string? CrewId);
    public sealed record CreateIncident(string ScenarioId, double ClockRate = 1);
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

        api.MapPost("/", async (CreateIncident r, IncidentService svc, HttpContext http, CancellationToken ct) =>
        {
            var incident = await svc.CreateAsync(r.ScenarioId, r.ClockRate, Actor.From(http.User), ct);
            return Results.Created($"/api/incidents/{incident.Id}", new { incident.Id, incident.Name, incident.StartedAt, incident.ClockRate });
        }).RequireAuthorization(Policies.Command);

        api.MapGet("/", async (LookoutDb db, CancellationToken ct) =>
            await db.Incidents.AsNoTracking().OrderByDescending(i => i.CreatedAt)
                .Select(i => new { i.Id, i.ScenarioId, i.Name, i.StartedAt, i.ClockRate, Status = i.Status.ToString().ToLower() })
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

        api.MapPost("/{id:guid}/alerts/{alertId:guid}/ack", async (Guid id, Guid alertId, VersionedAction r, AlertService svc, HttpContext http, CancellationToken ct) =>
            AlertDto.From(await svc.AcknowledgeAsync(id, alertId, r.Version, Actor.From(http.User), ct)))
            .RequireAuthorization(Policies.Field);

        api.MapPost("/{id:guid}/alerts/{alertId:guid}/cancel", async (Guid id, Guid alertId, VersionedAction r, AlertService svc, HttpContext http, CancellationToken ct) =>
            AlertDto.From(await svc.CancelAsync(id, alertId, r.Version, r.Reason ?? "", Actor.From(http.User), ct)))
            .RequireAuthorization(Policies.Command);

        api.MapGet("/{id:guid}/audit", async (Guid id, int? limit, LookoutDb db, CancellationToken ct) =>
            await db.AuditEvents.AsNoTracking().Where(e => e.IncidentId == id)
                .OrderByDescending(e => e.Time).ThenByDescending(e => e.Id).Take(Math.Clamp(limit ?? 200, 1, 1000))
                .Select(e => new { e.Id, e.Time, e.Type, e.Actor, e.Role, e.CrewId, e.AlertId, e.Details })
                .ToListAsync(ct))
            .RequireAuthorization(policy => policy.RequireRole(Roles.IncidentCommander, Roles.Observer));

        api.MapPut("/{id:guid}/routes/{routeId}", async (Guid id, string routeId, PutRoute r, IncidentService svc, HttpContext http, CancellationToken ct) =>
            RouteDto.From(await svc.PutRouteAsync(id, routeId, r.Name, r.SafetyZone, r.Path, r.Version, Actor.From(http.User), ct)))
            .RequireAuthorization(Policies.Command);

        api.MapPut("/{id:guid}/zones/{zoneId}", async (Guid id, string zoneId, PutZone r, IncidentService svc, HttpContext http, CancellationToken ct) =>
            ZoneDto.From(await svc.PutZoneAsync(id, zoneId, r.Name, r.Ring, r.Version, Actor.From(http.User), ct)))
            .RequireAuthorization(Policies.Command);
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
