using Lookout.Engine.Scenarios;

namespace Lookout.Engine.Rules;

/// <summary>
/// Rule 3, communications (the C in LCES; Watch Out #7). The crew's GPS fixes arrive over the
/// constrained link; a stale fix means the IC is planning on an old position. Stale at 5 minutes,
/// escalated to the IC at 15 (the AFF dispatch check-in timer). Yarnell Hill had a 33-minute gap.
/// </summary>
public static class CommsRule
{
    public const string Id = "R3-COMMS";

    public static Finding? Evaluate(double nowMin, double? lastFixMin, AlertSettings settings)
    {
        if (lastFixMin is null)
            return new Finding(Id, AlertLevel.Warning, "No position received from crew");
        var age = nowMin - lastFixMin.Value;
        if (age >= settings.CommsLostMin)
            return new Finding(Id, AlertLevel.Warning, $"COMMS LOST: no position for {age:0} min, IC check in by radio");
        if (age >= settings.CommsStaleMin)
            return new Finding(Id, AlertLevel.Watch, $"Comms stale: last position {age:0} min old");
        return null;
    }
}
