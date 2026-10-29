namespace Lookout.Engine.Spread;

/// <summary>Piecewise-constant weather: conditions apply from <paramref name="StartMin"/> until the next phase.</summary>
public sealed record WindPhase(double StartMin, SpreadConditions Conditions);

public static class WindPhases
{
    /// <summary>The phase in effect at time <paramref name="t"/> (minutes since scenario start).</summary>
    public static WindPhase At(IReadOnlyList<WindPhase> phases, double t)
    {
        WindPhase current = phases[0];
        foreach (var p in phases)
        {
            if (p.StartMin <= t) current = p;
            else break;
        }
        return current;
    }
}
