using Lookout.Engine.Geo;
using NetTopologySuite.Geometries;

namespace Lookout.Api;

/// <summary>Local-frame geometry to GeoJSON-style [lon, lat] arrays for the wire.</summary>
public static class GeoDto
{
    /// <summary>GeoJSON MultiPolygon coordinates: polygons, then rings (exterior first), then [lon, lat].</summary>
    public static double[][][][] MultiPolygon(LocalFrame f, Geometry g)
    {
        if (g.IsEmpty) return [];
        return Enumerable.Range(0, g.NumGeometries)
            .Select(g.GetGeometryN)
            .OfType<Polygon>()
            .Select(p => new[] { Ring(f, p.ExteriorRing) }.Concat(p.InteriorRings.Select(r => Ring(f, r))).ToArray())
            .ToArray();
    }

    public static double[][] Ring(LocalFrame f, LineString ring) => ring.Coordinates.Select(c => Point(f, c)).ToArray();

    public static double[] Point(LocalFrame f, Coordinate c)
    {
        var (lon, lat) = f.ToLonLat(c);
        return [Math.Round(lon, 6), Math.Round(lat, 6)];
    }

    public static double? Finite(double v) => double.IsFinite(v) ? Math.Round(v, 2) : null;
}
