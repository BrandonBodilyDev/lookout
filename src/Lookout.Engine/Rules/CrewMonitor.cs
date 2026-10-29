using System.Text;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Rules;

/// <summary>
/// Watches one crew. Each call runs the three rules on what the engine currently knows (front,
/// wind, the crew's latest fix) and reports a level change through the hysteresis latch, with the
/// alert text ready for the link. The replay simulator and the live incident runner both use this,
/// so a scenario replay and a live run of the same scenario produce the same alerts.
/// </summary>
public sealed class CrewMonitor(string crewId, AlertSettings settings, SpreadGrid? grid)
{
    private const double MinProgressM = 10.0;
    private readonly AlertLatch _latch = new(settings.DowngradeHoldMin);
    private readonly Dictionary<string, (double FixMin, double S, bool Moving)> _progress = new();

    public string CrewId { get; } = crewId;
    public AlertLevel Reported => _latch.Reported;

    /// <summary>The alert for <paramref name="change"/> could not be recorded: forget it so the next call raises it again.</summary>
    public void Revert(AlertChange change) => _latch.Reset(change.From);

    /// <summary>After a restart: carry on from the level last sent to the crew instead of from None.</summary>
    public void Seed(AlertLevel lastReported) => _latch.Reset(lastReported);

    /// <summary>Single-route convenience overload.</summary>
    public CrewAssessment Assess(
        RouteProfile profile, SafetyZone zone, Coordinate known, double? fixMin,
        Geometry front, SpreadConditions wind, double t, Func<double, string> clock) =>
        Assess([new RouteOption(profile, zone)], known, fixMin, front, wind, t, clock);

    /// <summary>
    /// Evaluate every escape route and work from the best one: a route the crew is already in the
    /// safety zone of, otherwise the one with the largest margin. Alternatives whose margin is under
    /// the buffer are named in the alert ("Avoid ER-1B (cut)"), so nobody takes them under pressure.
    /// </summary>
    /// <param name="clock">Formats a scenario minute as local time, e.g. "14:28".</param>
    public CrewAssessment Assess(
        IReadOnlyList<RouteOption> routes, Coordinate known, double? fixMin,
        Geometry front, SpreadConditions wind, double t, Func<double, string> clock)
    {
        if (routes.Count == 0) throw new ArgumentException("A crew needs at least one escape route.", nameof(routes));
        var results = routes.Select(o => (Option: o, Cutoff: grid is null
                ? CutoffRule.Evaluate(o.Profile, o.Zone, known, front, wind, settings, t)
                : GridCutoffRule.Evaluate(grid, o.Profile, o.Zone, known, front, wind, settings, t)))
            .ToList();
        var best = results.FirstOrDefault(r => r.Cutoff.InSafetyZone);
        if (best.Option is null) best = results.MaxBy(r => r.Cutoff.MarginMin);
        // Alternatives that would not leave the buffer are named, so nobody takes them under pressure.
        var unsafeAlternatives = results
            .Where(r => r.Option != best.Option && !r.Cutoff.InSafetyZone && r.Cutoff.MarginMin < settings.BufferMin)
            .Select(r => r.Cutoff.MarginMin < 0 ? $"{r.Option.Profile.Route.Id} (cut)" : $"{r.Option.Profile.Route.Id} ({r.Cutoff.MarginMin:0} min margin)")
            .ToList();

        var moving = routes.ToDictionary(o => o.Profile.Route.Id, o => UpdateProgress(o.Profile, known, fixMin));
        var enRoute = moving[best.Option.Profile.Route.Id];
        var cutoff = best.Cutoff;

        var findings = new List<Finding>();
        if (cutoff.Level > AlertLevel.None)
        {
            var summary = CutoffSummary(cutoff, best.Option.Profile.Route, t, enRoute, clock);
            if (unsafeAlternatives.Count > 0) summary += $". Avoid {string.Join(", ", unsafeAlternatives)}";
            findings.Add(new Finding(CutoffRule.Id, cutoff.Level, summary));
        }
        if (!cutoff.InSafetyZone && UnburnedFuelRule.Evaluate(known, front, wind, settings) is { } wo11)
            findings.Add(wo11);
        if (CommsRule.Evaluate(t, fixMin, settings) is { } comms)
            findings.Add(comms);

        var level = findings.Count == 0 ? AlertLevel.None : findings.Max(f => f.Level);
        var before = _latch.Reported;
        AlertChange? change = null;
        if (_latch.Update(level, t))
        {
            var text = AlertText(_latch.Reported, cutoff, findings);
            change = new AlertChange(before, _latch.Reported, text, Encoding.UTF8.GetByteCount(text));
        }
        var alternatives = results.Select(r => new RouteMargin(r.Option.Profile.Route.Id, r.Cutoff.InSafetyZone ? double.PositiveInfinity : r.Cutoff.MarginMin)).ToList();
        return new CrewAssessment(cutoff, findings, level, enRoute, change, best.Option.Profile.Route.Id, alternatives);
    }

    /// <summary>
    /// A crew is en route when its latest fix is further along the escape route than the fix before
    /// it. Then "leave by" no longer applies; the useful numbers are arrival time and margin.
    /// </summary>
    private bool UpdateProgress(RouteProfile profile, Coordinate known, double? fixMin)
    {
        if (fixMin is null) return false;
        var key = profile.Route.Id;
        if (_progress.TryGetValue(key, out var p) && p.FixMin == fixMin) return p.Moving;
        var s = profile.Project(known);
        var moving = _progress.TryGetValue(key, out var prev) && s - prev.S > MinProgressM;
        _progress[key] = (fixMin.Value, s, moving);
        return moving;
    }

    private static string CutoffSummary(CutoffResult c, EscapeRoute route, double t, bool enRoute, Func<double, string> clock) =>
        enRoute
            ? $"En route via {route.Id} to {c.SafetyZoneId}, arrive {clock(t + c.CrewTravelMin)}, margin {c.MarginMin:0} min"
            : c.SlackMin <= 0
            ? $"LEAVE NOW via {route.Id} to {c.SafetyZoneId}. Fire at route {clock(c.FireEtaAtCutMin)}, margin {c.MarginMin:0} min"
            : $"Leave by {clock(c.LeaveByMin)} via {route.Id} to {c.SafetyZoneId}. Fire at route {clock(c.FireEtaAtCutMin)}";

    private string AlertText(AlertLevel to, CutoffResult cutoff, List<Finding> findings) =>
        to == AlertLevel.None
            ? cutoff.InSafetyZone ? $"{CrewId} in safety zone {cutoff.SafetyZoneId}. All clear." : $"{CrewId} no active alerts."
            : $"{CrewId} {to.ToString().ToUpperInvariant()}: " +
              string.Join(". ", findings.OrderByDescending(f => f.Level).Select(f => f.Summary));
}

/// <summary>One escape route a crew may take, with the safety zone it ends in.</summary>
public sealed record RouteOption(RouteProfile Profile, SafetyZone Zone);

/// <param name="MarginMin">Safety margin on this route if the crew left now; infinity when already in its safety zone.</param>
public sealed record RouteMargin(string RouteId, double MarginMin);

/// <param name="RawLevel">Level before hysteresis.</param>
/// <param name="Change">Set when the reported level changed on this call: the alert to send.</param>
/// <param name="RouteId">The route the assessment (and any alert) is based on: the best one now.</param>
/// <param name="Routes">Every route's margin, best or not.</param>
public sealed record CrewAssessment(
    CutoffResult Cutoff, IReadOnlyList<Finding> Findings, AlertLevel RawLevel, bool EnRoute, AlertChange? Change,
    string RouteId, IReadOnlyList<RouteMargin> Routes);

public sealed record AlertChange(AlertLevel From, AlertLevel To, string Text, int Bytes);
