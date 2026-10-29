using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using Lookout.Engine.Travel;
using NetTopologySuite.Densify;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Rules;

/// <summary>
/// Rule 1, escape-route cut-off. For every point on the crew's remaining route, compare the
/// predicted fire arrival time T1 with the crew's arrival time T2 if they leave now
/// (Beighley 1995; Fryer, Dennison and Cova 2013). The safety margin is min(T1 - T2) over the
/// route; the crew must leave by now + margin - buffer. The alert level comes from the minutes
/// left until that leave-by time.
///
/// Fire prediction uses the current front and the wind observed right now, held constant: the
/// engine does not forecast wind shifts. That is deliberate. It reacts within one update to a
/// shift, and a forecast feed can be added later without changing the rule.
/// </summary>
public static class CutoffRule
{
    public const string Id = "R1-CUTOFF";
    private const double FrontDensifyM = 20.0;

    public static CutoffResult Evaluate(
        RouteProfile profile, SafetyZone zone, Coordinate crewPosition, Geometry front,
        SpreadConditions wind, AlertSettings settings, double nowMin)
    {
        var gf = front.Factory;
        var crewPoint = gf.CreatePoint(crewPosition);
        if (zone.Area.Contains(crewPoint))
            return CutoffResult.Safe(zone.Id);

        var frontVertices = FrontVertices(front);
        double FireEta(Coordinate p) => PredictFireMinutes(front, frontVertices, p, wind);

        var candidates = RemainingRoute(profile, crewPosition, settings);

        var margin = double.PositiveInfinity;
        Coordinate cut = candidates[^1].P;
        double fireAtCut = double.PositiveInfinity, crewAtCut = 0;
        foreach (var (p, crewMin) in candidates)
        {
            var fireMin = FireEta(p);
            if (fireMin - crewMin < margin)
            {
                margin = fireMin - crewMin;
                cut = p;
                fireAtCut = fireMin;
                crewAtCut = crewMin;
            }
        }

        return Result(zone, settings, nowMin, margin, cut, fireAtCut, crewAtCut, candidates[^1].CrewMin, null);
    }

    /// <summary>
    /// The crew's remaining route: the point where they join it (reached off-trail at flat speed
    /// times the off-trail factor), then every sample beyond it, each with crew minutes from now.
    /// </summary>
    public static List<(Coordinate P, double CrewMin)> RemainingRoute(RouteProfile profile, Coordinate crewPosition, AlertSettings settings)
    {
        var s0 = profile.Project(crewPosition);
        var onRoute = profile.PointAt(s0);
        var offRouteMin = crewPosition.Distance(onRoute) / (TravelRate.SpeedMs(0, profile.Pace) * settings.OffTrailFactor) / 60.0;
        var travelAtS0 = profile.TravelMinutesTo(s0);
        var candidates = new List<(Coordinate P, double CrewMin)> { (onRoute, offRouteMin) };
        candidates.AddRange(profile.Samples
            .Where(s => s.S > s0)
            .Select(s => (s.Point, offRouteMin + s.TravelMin - travelAtS0)));
        return candidates;
    }

    public static AlertLevel LevelFor(double slackMin, AlertSettings settings) =>
        slackMin < settings.CriticalMin ? AlertLevel.Critical
        : slackMin < settings.WarningMin ? AlertLevel.Warning
        : slackMin < settings.WatchMin ? AlertLevel.Watch
        : AlertLevel.None;

    internal static CutoffResult Result(
        SafetyZone zone, AlertSettings settings, double nowMin, double margin, Coordinate cut,
        double fireAtCut, double crewAtCut, double crewTravel, Lazy<Geometry?>? triggerZone)
    {
        var slack = margin - settings.BufferMin;
        return new CutoffResult(
            InSafetyZone: false,
            SafetyZoneId: zone.Id,
            MarginMin: margin,
            SlackMin: slack,
            LeaveByMin: nowMin + slack,
            CutPoint: cut,
            FireEtaAtCutMin: nowMin + fireAtCut,
            CrewTravelMin: crewTravel,
            CrewMinToCut: crewAtCut,
            Level: LevelFor(slack, settings),
            LazyTriggerZone: triggerZone);
    }

    /// <summary>
    /// Predicted minutes until the fire reaches <paramref name="p"/>, assuming the current wind holds:
    /// the fastest elliptical spread time from any point on the current front (Huygens' principle).
    /// </summary>
    public static double PredictFireMinutes(Geometry front, Coordinate p, SpreadConditions wind) =>
        PredictFireMinutes(front, FrontVertices(front), p, wind);

    private static Coordinate[] FrontVertices(Geometry front) =>
        front.IsEmpty ? [] : Densifier.Densify(front.Boundary, FrontDensifyM).Coordinates;

    private static double PredictFireMinutes(Geometry front, Coordinate[] vertices, Coordinate p, SpreadConditions wind)
    {
        if (vertices.Length == 0) return double.PositiveInfinity;
        if (front.Contains(front.Factory.CreatePoint(p))) return 0;
        var best = double.PositiveInfinity;
        foreach (var f in vertices)
            best = Math.Min(best, EllipseModel.TravelMinutes(f, p, wind));
        return best;
    }
}

/// <param name="MarginMin">min over remaining route of (fire arrival - crew arrival), leaving now.</param>
/// <param name="SlackMin">Margin minus buffer: minutes left before the crew must start moving.</param>
/// <param name="LeaveByMin">Scenario minute by which the crew must leave.</param>
/// <param name="CutPoint">Route point with the smallest margin: where the fire would cut the route.</param>
/// <param name="FireEtaAtCutMin">Scenario minute the fire is predicted at the cut point.</param>
/// <param name="CrewTravelMin">Minutes for the crew to reach the safety zone if they leave now.</param>
/// <param name="TriggerZone">Grid engine only: where the fire front would force the crew to leave immediately.</param>
public sealed record CutoffResult(
    bool InSafetyZone, string SafetyZoneId, double MarginMin, double SlackMin, double LeaveByMin,
    Coordinate? CutPoint, double FireEtaAtCutMin, double CrewTravelMin, double CrewMinToCut, AlertLevel Level,
    Lazy<Geometry?>? LazyTriggerZone = null)
{
    /// <summary>Built only when read: most evaluated routes' zones are never drawn.</summary>
    public Geometry? TriggerZone => LazyTriggerZone?.Value;

    public static CutoffResult Safe(string zoneId) =>
        new(true, zoneId, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity,
            null, double.PositiveInfinity, 0, 0, AlertLevel.None);
}
