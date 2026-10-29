using Lookout.Api.Data;
using Lookout.Engine.Geo;
using Lookout.Engine.Rules;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using Lookout.Link;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;

namespace Lookout.Api.Live;

/// <summary>
/// Everything the engine knows about one live incident, in the scenario's local metric frame:
/// the IC's routes and zones (from the database), the latest drone front, the latest wind, and
/// each crew's latest fix. Only the live runner's loop touches this, so it needs no locking.
/// </summary>
public sealed class IncidentRuntime
{
    private static readonly GeometryFactory Local = new();

    public IncidentRuntime(Incident incident, Scenario scenario, GovernorSettings governor)
    {
        Incident = incident;
        Scenario = scenario;
        Grid = scenario.Terrain is { } t ? new SpreadGrid(t) : null;
        Governor = new LinkGovernor(governor);
        Wind = scenario.Wind[0].Conditions;
    }

    public Incident Incident { get; }
    public Scenario Scenario { get; }
    public SpreadGrid? Grid { get; }
    public LinkGovernor Governor { get; }
    public LocalFrame Frame => Scenario.Frame;

    public Geometry Front { get; private set; } = Local.CreatePolygon();
    public DateTimeOffset? FrontTime { get; private set; }
    public string? FrontSource { get; private set; }
    public SpreadConditions Wind { get; private set; }
    public string WindSource { get; private set; } = "scenario default";
    public Dictionary<string, (double ScenarioMin, Coordinate Position, int Battery)> Fixes { get; } = new();
    public Dictionary<string, CrewMonitor> Monitors { get; } = new();
    public Dictionary<string, RouteProfile> Profiles { get; } = new();
    public Dictionary<string, SafetyZone> Zones { get; } = new();
    public List<CrewRecord> Crews { get; } = [];
    public Dictionary<string, CrewAssessment> LastAssessment { get; } = new();
    public bool Closed { get; set; }

    /// <summary>Scenario minute that corresponds to wall-clock <paramref name="now"/>.</summary>
    public double ScenarioMinute(DateTimeOffset now) => (now - Incident.StartedAt).TotalMinutes * Incident.ClockRate;

    public DateTimeOffset WallClock(double scenarioMin) => Incident.StartedAt.AddMinutes(scenarioMin / Incident.ClockRate);

    public string ClockLabel(double scenarioMin) =>
        double.IsFinite(scenarioMin) ? Scenario.Start.AddMinutes(scenarioMin).ToString("HH:mm") : "--:--";

    /// <summary>Rebuild routes, zones and crew monitors from the database rows (after an IC edit).</summary>
    public void LoadLayout(IEnumerable<CrewRecord> crews, IEnumerable<EscapeRouteRecord> routes, IEnumerable<SafetyZoneRecord> zones)
    {
        Crews.Clear();
        Crews.AddRange(crews.OrderBy(c => c.CrewId));
        Zones.Clear();
        foreach (var z in zones)
            Zones[z.ZoneId] = new SafetyZone(z.ZoneId, z.Name,
                Local.CreatePolygon(z.Area.ExteriorRing.Coordinates.Select(c => Frame.ToLocal(c.X, c.Y)).ToArray()));

        var routeById = routes.ToDictionary(r => r.RouteId);
        Profiles.Clear();
        foreach (var crew in Crews)
        {
            if (!routeById.TryGetValue(crew.RouteId, out var r)) continue;
            var route = new EscapeRoute(r.RouteId, r.Name, r.SafetyZoneId,
                r.Path.Coordinates.Select(c => { var l = Frame.ToLocal(c.X, c.Y); return new CoordinateZ(l.X, l.Y, double.IsNaN(c.Z) ? 0 : c.Z); }).ToList());
            Profiles[crew.CrewId] = new RouteProfile(route, Enum.Parse<Engine.Travel.PaceProfile>(crew.Pace, ignoreCase: true));
            if (!Monitors.ContainsKey(crew.CrewId))
                Monitors[crew.CrewId] = new CrewMonitor(crew.CrewId, Scenario.Alerts, Grid);
        }
    }

    /// <summary>
    /// Accept a surveyed front. Input from a drone link is not trusted: rings are closed if needed,
    /// degenerate rings are skipped, self-intersecting polygons are repaired, and anything still
    /// unusable is rejected with a FormatException so the previous front stays in place.
    /// </summary>
    public void UpdateFront(DroneFrontMessage m)
    {
        var polygons = new List<Geometry>();
        foreach (var poly in m.Coordinates ?? [])
        {
            if (poly is not { Length: > 0 } || poly[0] is not { Length: >= 3 } ring) continue;
            var coords = ring.Where(p => p is { Length: >= 2 } && double.IsFinite(p[0]) && double.IsFinite(p[1]))
                .Select(p => Frame.ToLocal(p[0], p[1])).ToList();
            if (coords.Count < 3) continue;
            if (!coords[0].Equals2D(coords[^1])) coords.Add(coords[0].Copy());
            if (coords.Count < 4) continue;
            Geometry g = Local.CreatePolygon(coords.ToArray());
            if (!g.IsValid) g = g.Buffer(0);
            if (!g.IsEmpty) polygons.Add(g);
        }
        Geometry front;
        try
        {
            front = polygons.Count == 0 ? Local.CreatePolygon() : CascadedPolygonUnion.Union(polygons);
        }
        catch (Exception e) when (e is NetTopologySuite.Geometries.TopologyException or ArgumentException)
        {
            throw new FormatException($"Unusable fire-front geometry: {e.Message}", e);
        }
        if (m.Coordinates is { Length: > 0 } && front.IsEmpty)
            throw new FormatException("Fire-front message had polygons but none were usable.");
        Front = front;
        FrontTime = m.Time;
        FrontSource = m.Source;
    }

    public void UpdateWind(SpreadConditions wind, string source)
    {
        Wind = wind;
        WindSource = source;
    }
}
