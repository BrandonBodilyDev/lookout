using Lookout.Engine.Geo;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Spread;

/// <summary>
/// Elliptical fire-shape model (Anderson 1983) with the length-to-breadth relationship used in
/// FARSITE (Finney 1998, RMRS-RP-4, eq. 13). A fire growing from a point under constant wind and
/// uniform fuel is an ellipse with the ignition point at its rear focus; spread rate along the ray
/// at angle gamma from the head is the polar equation of that ellipse from the focus
/// (Andrews 2018, GTR-371, section 6.2):
///
///     R(gamma) = R_H * (1 - e) / (1 - e * cos(gamma)),   e = sqrt(LB^2 - 1) / LB
///
/// gamma = 0 gives the head rate R_H; gamma = pi gives the backing rate R_H (1 - e) / (1 + e).
/// The head rate R_H is a scenario input. Lookout never derives it from fuel moisture.
/// </summary>
public static class EllipseModel
{
    public const double MaxLengthToBreadth = 8.0;

    /// <summary>FARSITE eq. 13. <paramref name="midflameWindMs"/> is effective midflame wind in m/s.</summary>
    public static double LengthToBreadth(double midflameWindMs)
    {
        var u = Math.Max(0.0, midflameWindMs);
        var lb = 0.936 * Math.Exp(0.2566 * u) + 0.461 * Math.Exp(-0.1548 * u) - 0.397;
        return Math.Clamp(lb, 1.0, MaxLengthToBreadth);
    }

    public static double Eccentricity(double lengthToBreadth) =>
        Math.Sqrt(lengthToBreadth * lengthToBreadth - 1.0) / lengthToBreadth;

    /// <summary>Spread rate along a ray at <paramref name="gammaRad"/> from the head direction.</summary>
    public static double RateAtAngle(double headRate, double eccentricity, double gammaRad) =>
        headRate * (1.0 - eccentricity) / (1.0 - eccentricity * Math.Cos(gammaRad));

    public static double BackingRate(double headRate, double eccentricity) =>
        headRate * (1.0 - eccentricity) / (1.0 + eccentricity);

    /// <summary>
    /// Vertices of the fire ellipse after <paramref name="minutes"/> of growth from a point at the
    /// origin, as offsets in metres. Used as the Minkowski "brush" when growing a front.
    /// </summary>
    public static Coordinate[] Shape(SpreadConditions c, double minutes, int vertexCount = 72)
    {
        var e = Eccentricity(LengthToBreadth(c.MidflameWindMs));
        var head = Bearing.HeadDirectionFromWind(c.WindFromDeg);
        var pts = new Coordinate[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var gamma = 2.0 * Math.PI * i / vertexCount;
            var r = RateAtAngle(c.HeadRateMPerMin, e, gamma) * minutes;
            var (dx, dy) = Bearing.UnitVector(head + gamma * 180.0 / Math.PI);
            pts[i] = new Coordinate(dx * r, dy * r);
        }
        return pts;
    }

    /// <summary>Minutes for fire to travel from <paramref name="from"/> to <paramref name="to"/> under constant conditions.</summary>
    public static double TravelMinutes(Coordinate from, Coordinate to, SpreadConditions c)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 1e-9) return 0.0;
        var e = Eccentricity(LengthToBreadth(c.MidflameWindMs));
        var head = Bearing.HeadDirectionFromWind(c.WindFromDeg);
        var gamma = Bearing.ToRadians(Bearing.Difference(Bearing.Of(dx, dy), head));
        return dist / RateAtAngle(c.HeadRateMPerMin, e, gamma);
    }
}

/// <summary>Conditions that drive spread during one wind phase.</summary>
/// <param name="WindFromDeg">Direction the wind blows from, degrees true.</param>
/// <param name="MidflameWindMs">Effective midflame wind speed, m/s (drives ellipse shape).</param>
/// <param name="HeadRateMPerMin">Head-fire rate of spread, m/min (scenario input).</param>
public sealed record SpreadConditions(double WindFromDeg, double MidflameWindMs, double HeadRateMPerMin);
