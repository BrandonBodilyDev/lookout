using Lookout.Engine.Geo;
using Lookout.Engine.Rules;
using Lookout.Engine.Simulation;
using NetTopologySuite.Geometries;

namespace Lookout.Api;

/// <summary>Wire shape for the dashboard: everything in [lon, lat], times in scenario minutes plus clock strings.</summary>
public static class TimelineDto
{
    public static object From(SimulationResult r)
    {
        var s = r.Scenario;
        var f = s.Frame;

        return new
        {
            s.Id,
            s.Name,
            s.Description,
            Start = s.Start,
            s.DurationMin,
            s.StepMin,
            Origin = new[] { f.OriginLon, f.OriginLat },
            SafetyZones = s.SafetyZones.Select(z => new { z.Id, z.Name, Ring = Ring(f, z.Area.ExteriorRing) }),
            Routes = s.Routes.Select(rt => new
            {
                rt.Id,
                rt.Name,
                SafetyZone = rt.SafetyZoneId,
                Path = rt.Vertices.Select(v => { var (lon, lat) = f.ToLonLat(v); return new[] { R6(lon), R6(lat), Math.Round(v.Z, 1) }; }),
            }),
            Crews = s.Crews.Select(c => new { c.Id, c.Name, Route = c.RouteId, c.Size, Pace = c.Pace.ToString().ToLowerInvariant() }),
            Frames = r.Frames.Select(fr => new
            {
                fr.T,
                Clock = Simulator.Clock(s, fr.T),
                Wind = new { fr.Wind.WindFromDeg, fr.Wind.MidflameWindMs, fr.Wind.HeadRateMPerMin },
                Fire = fr.Front.IsEmpty ? [] : Ring(f, ((Polygon)fr.Front).ExteriorRing),
                Crews = fr.Crews.Select(c => new
                {
                    Id = c.CrewId,
                    Truth = Point(f, c.TruePosition),
                    Known = Point(f, c.KnownPosition),
                    FixAgeMin = c.LastFixMin is { } lf ? fr.T - lf : (double?)null,
                    c.EnRoute,
                    Level = c.Level.ToString().ToLowerInvariant(),
                    c.Cutoff.InSafetyZone,
                    MarginMin = Finite(c.Cutoff.MarginMin),
                    SlackMin = Finite(c.Cutoff.SlackMin),
                    LeaveBy = double.IsFinite(c.Cutoff.LeaveByMin) ? Simulator.Clock(s, c.Cutoff.LeaveByMin) : null,
                    FireAtCut = double.IsFinite(c.Cutoff.FireEtaAtCutMin) ? Simulator.Clock(s, c.Cutoff.FireEtaAtCutMin) : null,
                    Cut = c.Cutoff.CutPoint is { } cp ? Point(f, cp) : null,
                    Findings = c.Findings.Select(x => new { x.Rule, Level = x.Level.ToString().ToLowerInvariant(), x.Summary }),
                    c.BurnedOver,
                }),
            }),
            Alerts = r.Alerts.Select(a => new
            {
                a.T,
                Clock = a.Clock.ToString("HH:mm:ss"),
                Crew = a.CrewId,
                Route = a.RouteId,
                From = a.From.ToString().ToLowerInvariant(),
                To = a.To.ToString().ToLowerInvariant(),
                a.Text,
                a.Bytes,
            }),
            Score = new
            {
                Routes = r.Routes.Select(x => new { x.RouteId, x.TruthCutMin, TruthCut = ClockOrNull(s, x.TruthCutMin) }),
                Crews = r.Crews.Select(x => new
                {
                    x.CrewId,
                    x.ReachedSafetyZoneMin,
                    ReachedSafetyZone = ClockOrNull(s, x.ReachedSafetyZoneMin),
                    x.BurnoverMin,
                    Burnover = ClockOrNull(s, x.BurnoverMin),
                }),
            },
        };
    }

    private static double[][] Ring(LocalFrame f, LineString ring) =>
        ring.Coordinates.Select(c => Point(f, c)).ToArray();

    private static double[] Point(LocalFrame f, Coordinate c)
    {
        var (lon, lat) = f.ToLonLat(c);
        return [R6(lon), R6(lat)];
    }

    private static string? ClockOrNull(Lookout.Engine.Scenarios.Scenario s, double? minute) =>
        minute is { } m ? s.Start.AddMinutes(m).ToString("HH:mm") : null;

    private static double R6(double v) => Math.Round(v, 6);

    private static double? Finite(double v) => double.IsFinite(v) ? Math.Round(v, 2) : null;
}
