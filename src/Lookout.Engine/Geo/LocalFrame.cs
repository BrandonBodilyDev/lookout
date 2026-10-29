using NetTopologySuite.Geometries;

namespace Lookout.Engine.Geo;

/// <summary>
/// Converts between WGS84 lon/lat and a local metric frame (x = meters east, y = meters north)
/// centred on the scenario origin. Equirectangular projection: error is well under 0.1% across
/// the few kilometres of a scenario, which is far below the uncertainty of every other input.
/// All engine math runs in this frame; lon/lat only appear at the file and API boundaries.
/// </summary>
public sealed class LocalFrame
{
    private const double MetersPerDegreeLat = 111_320.0;
    private readonly double _metersPerDegreeLon;

    public LocalFrame(double originLon, double originLat)
    {
        OriginLon = originLon;
        OriginLat = originLat;
        _metersPerDegreeLon = MetersPerDegreeLat * Math.Cos(originLat * Math.PI / 180.0);
    }

    public double OriginLon { get; }
    public double OriginLat { get; }

    public Coordinate ToLocal(double lon, double lat) =>
        new((lon - OriginLon) * _metersPerDegreeLon, (lat - OriginLat) * MetersPerDegreeLat);

    public (double Lon, double Lat) ToLonLat(Coordinate c) =>
        (OriginLon + c.X / _metersPerDegreeLon, OriginLat + c.Y / MetersPerDegreeLat);
}
