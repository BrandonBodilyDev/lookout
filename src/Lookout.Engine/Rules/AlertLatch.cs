namespace Lookout.Engine.Rules;

/// <summary>
/// Hysteresis for alert levels, to limit alarm fatigue. Escalation is reported at once. A
/// downgrade is reported only after the lower level has held continuously for the hold time, so
/// a margin hovering on a threshold does not flap WARNING / CRITICAL / WARNING over the link.
/// </summary>
public sealed class AlertLatch(double downgradeHoldMin)
{
    private (AlertLevel Level, double Since)? _pendingDowngrade;

    public AlertLevel Reported { get; private set; } = AlertLevel.None;

    /// <summary>Feed the raw level at <paramref name="t"/>; returns true when the reported level changes.</summary>
    public bool Update(AlertLevel raw, double t)
    {
        if (raw > Reported)
        {
            Reported = raw;
            _pendingDowngrade = null;
            return true;
        }
        if (raw == Reported)
        {
            _pendingDowngrade = null;
            return false;
        }
        // raw < Reported: start or continue the hold. The hold restarts if the target changes.
        if (_pendingDowngrade is not { } p || p.Level != raw) _pendingDowngrade = (raw, t);
        if (t - _pendingDowngrade.Value.Since + 1e-9 < downgradeHoldMin) return false;
        Reported = raw;
        _pendingDowngrade = null;
        return true;
    }
}
