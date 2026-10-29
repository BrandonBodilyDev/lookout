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
    /// <param name="observedFront">
    /// What the drone reports at a scenario minute. Defaults to the scenario's true fire; the drone
    /// pipeline passes a camera-and-detector survey here to measure what detection costs.
    /// </param>
    public static SimulationResult Run(Scenario scenario, Func<double, Geometry>? observedFront = null)
    {
        observedFront ??= scenario.Fire.FrontAt;
        var grid = scenario.Terrain is { } terrain ? new SpreadGrid(terrain) : null;
        var profiles = scenario.Crews.ToDictionary(
            c => c.Id, c => new RouteProfile(scenario.Route(c.RouteId), c.Pace));

        var frames = new List<Frame>();
        var alerts = new List<AlertEvent>();
        var monitors = scenario.Crews.ToDictionary(c => c.Id, c => new CrewMonitor(c.Id, scenario.Alerts, grid));
        string ClockOf(double m) => Clock(scenario, m);

        var steps = (int)Math.Round(scenario.DurationMin / scenario.StepMin);
        for (var i = 0; i <= steps; i++)
        {
            var t = i * scenario.StepMin;
            var front = observedFront(t);
            var truthFront = scenario.Fire.FrontAt(t);
            var wind = WindPhases.At(scenario.Wind, t).Conditions;
            var crewStates = new List<CrewState>();

            foreach (var crew in scenario.Crews)
            {
                var profile = profiles[crew.Id];
                var zone = scenario.SafetyZone(profile.Route.SafetyZoneId);
                var fix = crew.LastFixAt(t);
                var known = fix?.Position ?? crew.TruePositionAt(0);

                var a = monitors[crew.Id].Assess(profile, zone, known, fix?.T, front, wind, t, ClockOf);
                var truth = crew.TruePositionAt(t);
                crewStates.Add(new CrewState(
                    crew.Id, truth, known, fix?.T, a.EnRoute, a.Cutoff, a.Findings, a.RawLevel,
                    BurnedOver: !truthFront.IsEmpty && truthFront.Contains(truthFront.Factory.CreatePoint(truth))));

                if (a.Change is { } c)
                    alerts.Add(new AlertEvent(t, scenario.Start.AddMinutes(t), crew.Id, profile.Route.Id, c.From, c.To, c.Text, c.Bytes));
            }

            frames.Add(new Frame(t, front, wind, crewStates));
        }

        return new SimulationResult(scenario, frames, alerts, ScoreRoutes(scenario), ScoreCrews(scenario, frames));
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
