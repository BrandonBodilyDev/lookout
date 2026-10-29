using System.Text;
using Lookout.Engine.Rules;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Simulation;

/// <summary>
/// Steps a scenario through time. At each step the engine sees only what the field would send it:
/// the current fire front (the drone's survey), the current wind, and each crew's latest GPS fix.
/// It runs the rules and emits an alert event whenever a crew's overall level changes.
/// Scenario truth (where the fire and crews really are) is recorded alongside for scoring.
/// </summary>
public static class Simulator
{
    public static SimulationResult Run(Scenario scenario)
    {
        var grid = scenario.Terrain is { } terrain ? new SpreadGrid(terrain) : null;
        var profiles = scenario.Crews.ToDictionary(
            c => c.Id, c => new RouteProfile(scenario.Route(c.RouteId), c.Pace));

        var frames = new List<Frame>();
        var alerts = new List<AlertEvent>();
        var latches = scenario.Crews.ToDictionary(c => c.Id, _ => new AlertLatch(scenario.Alerts.DowngradeHoldMin));
        var progress = new Dictionary<string, Progress>();

        var steps = (int)Math.Round(scenario.DurationMin / scenario.StepMin);
        for (var i = 0; i <= steps; i++)
        {
            var t = i * scenario.StepMin;
            var front = scenario.Fire.FrontAt(t);
            var wind = WindPhases.At(scenario.Wind, t).Conditions;
            var crewStates = new List<CrewState>();

            foreach (var crew in scenario.Crews)
            {
                var profile = profiles[crew.Id];
                var zone = scenario.SafetyZone(profile.Route.SafetyZoneId);
                var fix = crew.LastFixAt(t);
                var known = fix?.Position ?? crew.TruePositionAt(0);

                var cutoff = grid is null
                    ? CutoffRule.Evaluate(profile, zone, known, front, wind, scenario.Alerts, t)
                    : GridCutoffRule.Evaluate(grid, profile, zone, known, front, wind, scenario.Alerts, t);
                var enRoute = IsEnRoute(profile, known, fix?.T, progress, crew.Id);
                var findings = new List<Finding>();
                if (cutoff.Level > AlertLevel.None)
                    findings.Add(new Finding(CutoffRule.Id, cutoff.Level, CutoffSummary(scenario, cutoff, profile.Route, t, enRoute)));
                if (!cutoff.InSafetyZone && UnburnedFuelRule.Evaluate(known, front, wind, scenario.Alerts) is { } wo11)
                    findings.Add(wo11);
                if (CommsRule.Evaluate(t, fix?.T, scenario.Alerts) is { } comms)
                    findings.Add(comms);

                var level = findings.Count == 0 ? AlertLevel.None : findings.Max(f => f.Level);
                var truth = crew.TruePositionAt(t);
                crewStates.Add(new CrewState(
                    crew.Id, truth, known, fix?.T, enRoute, cutoff, findings, level,
                    BurnedOver: !front.IsEmpty && front.Contains(front.Factory.CreatePoint(truth))));

                var latch = latches[crew.Id];
                var before = latch.Reported;
                if (latch.Update(level, t))
                    alerts.Add(BuildAlert(scenario, crew, profile.Route, t, before, latch.Reported, cutoff, findings));
            }

            frames.Add(new Frame(t, front, wind, crewStates));
        }

        return new SimulationResult(scenario, frames, alerts, ScoreRoutes(scenario), ScoreCrews(scenario, frames));
    }

    private const double MinProgressM = 10.0;

    /// <summary>
    /// A crew is en route when its latest fix is further along the escape route than the fix before
    /// it. Then "leave by" no longer applies; the useful numbers are arrival time and margin.
    /// </summary>
    private static bool IsEnRoute(
        RouteProfile profile, Coordinate known, double? fixMin, Dictionary<string, Progress> progress, string crewId)
    {
        if (fixMin is null) return false;
        var prev = progress.GetValueOrDefault(crewId);
        if (prev is not null && prev.FixMin == fixMin) return prev.Moving;
        var s = profile.Project(known);
        var moving = prev is not null && s - prev.S > MinProgressM;
        progress[crewId] = new Progress(fixMin.Value, s, moving);
        return moving;
    }

    private sealed record Progress(double FixMin, double S, bool Moving);

    private static string CutoffSummary(Scenario s, CutoffResult c, EscapeRoute route, double t, bool enRoute) =>
        enRoute
            ? $"En route via {route.Id} to {c.SafetyZoneId}, arrive {Clock(s, t + c.CrewTravelMin)}, margin {c.MarginMin:0} min"
            : c.SlackMin <= 0
            ? $"LEAVE NOW via {route.Id} to {c.SafetyZoneId}. Fire at route {Clock(s, c.FireEtaAtCutMin)}, margin {c.MarginMin:0} min"
            : $"Leave by {Clock(s, c.LeaveByMin)} via {route.Id} to {c.SafetyZoneId}. Fire at route {Clock(s, c.FireEtaAtCutMin)}";

    private static AlertEvent BuildAlert(
        Scenario s, Crew crew, EscapeRoute route, double t, AlertLevel from, AlertLevel to,
        CutoffResult cutoff, List<Finding> findings)
    {
        string text;
        if (to == AlertLevel.None)
            text = cutoff.InSafetyZone ? $"{crew.Id} in safety zone {cutoff.SafetyZoneId}. All clear." : $"{crew.Id} no active alerts.";
        else
            text = $"{crew.Id} {to.ToString().ToUpperInvariant()}: " +
                   string.Join(". ", findings.OrderByDescending(f => f.Level).Select(f => f.Summary));

        return new AlertEvent(t, s.Start.AddMinutes(t), crew.Id, route.Id, from, to, text, Encoding.UTF8.GetByteCount(text));
    }

    /// <summary>Truth: when the real fire first touches each route.</summary>
    private static IReadOnlyList<RouteScore> ScoreRoutes(Scenario s) =>
        s.Routes.Select(r => new RouteScore(r.Id, s.Fire.FirstArrival(r.Line, s.DurationMin))).ToList();

    private static IReadOnlyList<CrewScore> ScoreCrews(Scenario s, List<Frame> frames) =>
        s.Crews.Select(c =>
        {
            var zone = s.SafetyZone(s.Route(c.RouteId).SafetyZoneId);
            var states = frames.Select(f => (f.T, State: f.Crews.Single(x => x.CrewId == c.Id))).ToList();
            var reached = states.FirstOrDefault(x => zone.Area.Contains(zone.Area.Factory.CreatePoint(x.State.TruePosition)));
            var burned = states.FirstOrDefault(x => x.State.BurnedOver);
            return new CrewScore(c.Id, reached.State is null ? null : reached.T, burned.State is null ? null : burned.T);
        }).ToList();

    public static string Clock(Scenario s, double minute) =>
        double.IsFinite(minute) ? s.Start.AddMinutes(minute).ToString("HH:mm") : "--:--";
}

public sealed record SimulationResult(
    Scenario Scenario, IReadOnlyList<Frame> Frames, IReadOnlyList<AlertEvent> Alerts,
    IReadOnlyList<RouteScore> Routes, IReadOnlyList<CrewScore> Crews);

public sealed record Frame(double T, Geometry Front, SpreadConditions Wind, IReadOnlyList<CrewState> Crews);

/// <param name="TruePosition">Where the crew really is (scenario truth).</param>
/// <param name="KnownPosition">Where the engine thinks the crew is (latest fix).</param>
/// <param name="EnRoute">The crew's fixes show it moving along its escape route.</param>
public sealed record CrewState(
    string CrewId, Coordinate TruePosition, Coordinate KnownPosition, double? LastFixMin, bool EnRoute,
    CutoffResult Cutoff, IReadOnlyList<Finding> Findings, AlertLevel Level, bool BurnedOver);

/// <param name="Bytes">UTF-8 size of the text; Phase 1 caps crew-bound messages at 270 bytes.</param>
public sealed record AlertEvent(
    double T, DateTimeOffset Clock, string CrewId, string RouteId,
    AlertLevel From, AlertLevel To, string Text, int Bytes);

public sealed record RouteScore(string RouteId, double? TruthCutMin);

public sealed record CrewScore(string CrewId, double? ReachedSafetyZoneMin, double? BurnoverMin);
