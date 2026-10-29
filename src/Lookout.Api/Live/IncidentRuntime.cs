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

    public void UpdateFront(DroneFrontMessage m)
    {
        var polygons = m.Coordinates
            .Select(poly => (Geometry)Local.CreatePolygon(poly[0].Select(p => Frame.ToLocal(p[0], p[1])).ToArray()))
            .ToList();
        Front = polygons.Count == 0 ? Local.CreatePolygon() : CascadedPolygonUnion.Union(polygons);
        FrontTime = m.Time;
    }

    public void UpdateWind(SpreadConditions wind, string source)
    {
        Wind = wind;
        WindSource = source;
    }
}
