namespace Lookout.Engine.Travel;

public enum PaceProfile { Low, Moderate, High }

/// <summary>
/// Wildland firefighter travel rate vs terrain slope, from GPS tracking of eleven Type 1 hotshot
/// crews carrying gear on trails: Sullivan, Campbell, Dennison, Brewer, Butler (2020),
/// "Modeling wildland firefighter travel rates by terrain slope", Fire 3(3):52,
/// https://doi.org/10.3390/fire3030052. Lorentz form with a linear term, fitted at the 16.7th,
/// 50th and 83.3rd percentiles (Table 2):
///
///     v = c * 1 / (pi * b * (1 + ((theta - a) / b)^2)) + d + e * theta
///
/// theta = slope angle in degrees along the direction of travel (uphill positive), v in m/s.
/// "Low" is the conservative setting: the IRPG plans escape time on the slowest person.
/// The authors note the fits do not apply when packs are dropped or for off-trail travel.
/// </summary>
public static class TravelRate
{
    private sealed record Coefficients(double A, double B, double C, double D, double E);

    private static readonly Coefficients Low = new(-3.3717, 25.8255, 92.6594, -0.1624, 0.0019);
    private static readonly Coefficients Moderate = new(-2.8292, 20.9482, 77.6346, 0.2228, -0.0004);
    private static readonly Coefficients High = new(-2.2893, 19.4024, 65.3577, 0.6226, -0.0020);

    /// <summary>Floor so a very steep segment never produces zero or negative speed.</summary>
    public const double MinimumSpeedMs = 0.1;

    public static double SpeedMs(double slopeDeg, PaceProfile profile = PaceProfile.Moderate)
    {
        var k = profile switch
        {
            PaceProfile.Low => Low,
            PaceProfile.High => High,
            _ => Moderate,
        };
        var x = (slopeDeg - k.A) / k.B;
        var v = k.C / (Math.PI * k.B * (1.0 + x * x)) + k.D + k.E * slopeDeg;
        return Math.Max(MinimumSpeedMs, v);
    }

    /// <summary>Minutes to cover <paramref name="horizontalMeters"/> while climbing <paramref name="riseMeters"/>.</summary>
    public static double SegmentMinutes(double horizontalMeters, double riseMeters, PaceProfile profile, double speedFactor = 1.0)
    {
        if (horizontalMeters <= 0) return 0;
        var slopeDeg = Math.Atan2(riseMeters, horizontalMeters) * 180.0 / Math.PI;
        var pathMeters = Math.Sqrt(horizontalMeters * horizontalMeters + riseMeters * riseMeters);
        return pathMeters / (SpeedMs(slopeDeg, profile) * speedFactor) / 60.0;
    }
}
