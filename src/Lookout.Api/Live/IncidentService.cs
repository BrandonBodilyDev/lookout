using System.Text.Json;
using Lookout.Api.Auth;
using Lookout.Api.Data;
using Lookout.Engine.Geo;
using Lookout.Engine.Scenarios;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Lookout.Api.Live;

/// <summary>Creates incidents from scenarios and handles the IC's edits to routes and safety zones.</summary>
public sealed class IncidentService(LookoutDb db, ScenarioStore scenarios, TimeProvider clock)
{
    private static readonly GeometryFactory Wgs84 = new(new PrecisionModel(), 4326);

    public async Task<Incident> CreateAsync(string scenarioId, double clockRate, Actor actor, WindSource windSource = WindSource.Field, CancellationToken ct = default)
    {
        var s = scenarios.Load(scenarioId) ?? throw new NotFoundException($"Scenario {scenarioId}");
        if (clockRate is <= 0 or > 60) throw new ConflictException("Clock rate must be between 0 and 60 scenario minutes per real minute.");
        var now = clock.GetUtcNow();
        var incident = new Incident
        {
            Id = Guid.CreateVersion7(),
            ScenarioId = s.Id,
            Name = s.Name,
            CreatedAt = now,
            StartedAt = now,
            ClockRate = clockRate,
            WindSource = windSource,
        };
        db.Incidents.Add(incident);

        foreach (var c in s.Crews)
            db.Crews.Add(new CrewRecord
            {
                IncidentId = incident.Id, CrewId = c.Id, Name = c.Name, RouteId = c.RouteId,
                AlternateRouteIds = [.. c.AlternateRouteIds], Size = c.Size, Pace = c.Pace.ToString().ToLowerInvariant(),
            });
        foreach (var z in s.SafetyZones)
            db.SafetyZones.Add(new SafetyZoneRecord { IncidentId = incident.Id, ZoneId = z.Id, Name = z.Name, Area = ToWgs84(s.Frame, z.Area) });
        foreach (var r in s.Routes)
            db.EscapeRoutes.Add(new EscapeRouteRecord
            {
                IncidentId = incident.Id, RouteId = r.Id, Name = r.Name, SafetyZoneId = r.SafetyZoneId,
                Path = Wgs84.CreateLineString(r.Vertices.Select(v => { var (lon, lat) = s.Frame.ToLonLat(v); return (Coordinate)new CoordinateZ(lon, lat, v.Z); }).ToArray()),
            });

        Audit(incident.Id, "incident.created", actor, now, new { scenarioId, clockRate, windSource = windSource.ToString() });
        await db.SaveChangesAsync(ct);
        return incident;
    }

    /// <summary>
    /// Create or replace an escape route. Elevation along the new line comes from the scenario
    /// terrain. Updating needs the version the IC last saw; creating must not send one.
    /// </summary>
    public async Task<EscapeRouteRecord> PutRouteAsync(
        Guid incidentId, string routeId, string name, string safetyZoneId, IReadOnlyList<double[]> lonLat, uint? expectedVersion,
        Actor actor, CancellationToken ct = default)
    {
        if (lonLat.Count < 2) throw new ConflictException("A route needs at least two points.");
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == incidentId, ct) ?? throw new NotFoundException($"Incident {incidentId}");
        var zone = await db.SafetyZones.SingleOrDefaultAsync(z => z.IncidentId == incidentId && z.ZoneId == safetyZoneId, ct)
            ?? throw new ConflictException($"Safety zone {safetyZoneId} does not exist.");
        var path = Wgs84.CreateLineString(lonLat.Select(p => (Coordinate)new CoordinateZ(p[0], p[1], Elevation(incident.ScenarioId, p[0], p[1]))).ToArray());
        if (!zone.Area.Contains(Wgs84.CreatePoint(path.Coordinates[^1])))
            throw new ConflictException($"Route must end inside safety zone {safetyZoneId}.");

        var now = clock.GetUtcNow();
        var route = await db.EscapeRoutes.SingleOrDefaultAsync(r => r.IncidentId == incidentId && r.RouteId == routeId, ct);
        if (route is null)
        {
            if (expectedVersion is not null) throw new ConflictException($"Route {routeId} no longer exists.");
            route = new EscapeRouteRecord { IncidentId = incidentId, RouteId = routeId, Name = name, SafetyZoneId = safetyZoneId, Path = path };
            db.EscapeRoutes.Add(route);
            Audit(incidentId, "route.created", actor, now, new { routeId, safetyZoneId, points = lonLat.Count });
        }
        else
        {
            if (expectedVersion is null || expectedVersion != route.Version)
                throw new ConflictException($"Route {routeId} changed since you loaded it.") { Current = RouteDto.From(route) };
            db.Entry(route).Property(r => r.Version).OriginalValue = expectedVersion.Value;
            route.Name = name;
            route.SafetyZoneId = safetyZoneId;
            route.Path = path;
            Audit(incidentId, "route.updated", actor, now, new { routeId, safetyZoneId, points = lonLat.Count });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException($"Route {routeId} was changed by someone else at the same time.");
        }
        return route;
    }

    public async Task<SafetyZoneRecord> PutZoneAsync(
        Guid incidentId, string zoneId, string name, IReadOnlyList<double[]> ring, uint? expectedVersion, Actor actor, CancellationToken ct = default)
    {
        if (ring.Count < 3) throw new ConflictException("A safety zone needs at least three points.");
        _ = await db.Incidents.SingleOrDefaultAsync(i => i.Id == incidentId, ct) ?? throw new NotFoundException($"Incident {incidentId}");
        var coords = ring.Select(p => new Coordinate(p[0], p[1])).ToList();
        if (!coords[0].Equals2D(coords[^1])) coords.Add(coords[0].Copy());
        var area = Wgs84.CreatePolygon(coords.ToArray());
        if (!area.IsValid) throw new ConflictException("Safety zone outline crosses itself.");

        var now = clock.GetUtcNow();
        var zone = await db.SafetyZones.SingleOrDefaultAsync(z => z.IncidentId == incidentId && z.ZoneId == zoneId, ct);
        if (zone is null)
        {
            if (expectedVersion is not null) throw new ConflictException($"Safety zone {zoneId} no longer exists.");
            zone = new SafetyZoneRecord { IncidentId = incidentId, ZoneId = zoneId, Name = name, Area = area };
            db.SafetyZones.Add(zone);
            Audit(incidentId, "zone.created", actor, now, new { zoneId });
        }
        else
        {
            if (expectedVersion is null || expectedVersion != zone.Version)
                throw new ConflictException($"Safety zone {zoneId} changed since you loaded it.");
            db.Entry(zone).Property(z => z.Version).OriginalValue = expectedVersion.Value;
            zone.Name = name;
            zone.Area = area;
            Audit(incidentId, "zone.updated", actor, now, new { zoneId });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException($"Safety zone {zoneId} was changed by someone else at the same time.");
        }
        return zone;
    }

    private double Elevation(string scenarioId, double lon, double lat)
    {
        var s = scenarios.Load(scenarioId);
        if (s?.Terrain is null) return 0;
        var local = s.Frame.ToLocal(lon, lat);
        return Math.Round(s.Terrain.Elevation(local.X, local.Y), 1);
    }

    private static Polygon ToWgs84(LocalFrame frame, Polygon local) =>
        Wgs84.CreatePolygon(local.ExteriorRing.Coordinates.Select(c => { var (lon, lat) = frame.ToLonLat(c); return new Coordinate(lon, lat); }).ToArray());

    private void Audit(Guid incidentId, string type, Actor actor, DateTimeOffset now, object details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            Time = now, IncidentId = incidentId, Type = type, Actor = actor.Name, Role = actor.Role,
            Details = JsonSerializer.SerializeToDocument(details),
        });
}

public sealed record RouteDto(string Id, string Name, string SafetyZone, double[][] Path, uint Version)
{
    public static RouteDto From(EscapeRouteRecord r) =>
        new(r.RouteId, r.Name, r.SafetyZoneId, r.Path.Coordinates.Select(c => new[] { Math.Round(c.X, 6), Math.Round(c.Y, 6), Math.Round(c.Z, 1) }).ToArray(), r.Version);
}

public sealed record ZoneDto(string Id, string Name, double[][] Ring, uint Version)
{
    public static ZoneDto From(SafetyZoneRecord z) =>
        new(z.ZoneId, z.Name, z.Area.ExteriorRing.Coordinates.Select(c => new[] { Math.Round(c.X, 6), Math.Round(c.Y, 6) }).ToArray(), z.Version);
}
