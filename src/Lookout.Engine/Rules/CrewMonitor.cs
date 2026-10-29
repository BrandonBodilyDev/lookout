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
    private (double FixMin, double S, bool Moving)? _progress;

    public string CrewId { get; } = crewId;
    public AlertLevel Reported => _latch.Reported;

    /// <summary>The alert for <paramref name="change"/> could not be recorded: forget it so the next call raises it again.</summary>
    public void Revert(AlertChange change) => _latch.Reset(change.From);

    /// <summary>After a restart: carry on from the level last sent to the crew instead of from None.</summary>
    public void Seed(AlertLevel lastReported) => _latch.Reset(lastReported);

    /// <param name="clock">Formats a scenario minute as local time, e.g. "14:28".</param>
    public CrewAssessment Assess(
        RouteProfile profile, SafetyZone zone, Coordinate known, double? fixMin,
        Geometry front, SpreadConditions wind, double t, Func<double, string> clock)
    {
        var cutoff = grid is null
            ? CutoffRule.Evaluate(profile, zone, known, front, wind, settings, t)
            : GridCutoffRule.Evaluate(grid, profile, zone, known, front, wind, settings, t);
        var enRoute = UpdateProgress(profile, known, fixMin);

        var findings = new List<Finding>();
        if (cutoff.Level > AlertLevel.None)
            findings.Add(new Finding(CutoffRule.Id, cutoff.Level, CutoffSummary(cutoff, profile.Route, t, enRoute, clock)));
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
        return new CrewAssessment(cutoff, findings, level, enRoute, change);
    }

    /// <summary>
    /// A crew is en route when its latest fix is further along the escape route than the fix before
    /// it. Then "leave by" no longer applies; the useful numbers are arrival time and margin.
    /// </summary>
    private bool UpdateProgress(RouteProfile profile, Coordinate known, double? fixMin)
    {
        if (fixMin is null) return false;
        if (_progress is { } p && p.FixMin == fixMin) return p.Moving;
        var s = profile.Project(known);
        var moving = _progress is { } prev && s - prev.S > MinProgressM;
        _progress = (fixMin.Value, s, moving);
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

/// <param name="RawLevel">Level before hysteresis.</param>
/// <param name="Change">Set when the reported level changed on this call: the alert to send.</param>
public sealed record CrewAssessment(
    CutoffResult Cutoff, IReadOnlyList<Finding> Findings, AlertLevel RawLevel, bool EnRoute, AlertChange? Change);

public sealed record AlertChange(AlertLevel From, AlertLevel To, string Text, int Bytes);
