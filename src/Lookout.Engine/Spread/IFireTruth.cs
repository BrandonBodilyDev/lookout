using NetTopologySuite.Geometries;

namespace Lookout.Engine.Spread;

/// <summary>The scenario's real fire: what a drone survey would report, and what scoring checks against.</summary>
public interface IFireTruth
{
    /// <summary>Burned area at scenario minute <paramref name="t"/>.</summary>
    Geometry FrontAt(double t);

    /// <summary>First minute the fire touches <paramref name="line"/>, or null if not before <paramref name="horizonMin"/>.</summary>
    double? FirstArrival(LineString line, double horizonMin);
}
