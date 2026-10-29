using Lookout.Engine.Terrain;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;

namespace Lookout.Engine.Geo;

/// <summary>Turns a set of grid cells into a polygon: horizontal runs become rectangles, then one union.</summary>
public static class RasterPolygonizer
{
    private static readonly GeometryFactory Factory = new();

    public static Geometry Polygonize(TerrainGrid grid, Func<int, bool> include, double simplifyTolerance = 0)
    {
        var boxes = new List<Geometry>();
        for (var j = 0; j < grid.Ny; j++)
        {
            var i = 0;
            while (i < grid.Nx)
            {
                if (!include(grid.Index(i, j))) { i++; continue; }
                var start = i;
                while (i < grid.Nx && include(grid.Index(i, j))) i++;
                double x0 = grid.X0 + start * grid.CellSize, x1 = grid.X0 + i * grid.CellSize;
                double y0 = grid.Y0 + j * grid.CellSize, y1 = y0 + grid.CellSize;
                boxes.Add(Factory.ToGeometry(new Envelope(x0, x1, y0, y1)));
            }
        }
        if (boxes.Count == 0) return Factory.CreatePolygon();
        var union = CascadedPolygonUnion.Union(boxes);
        return simplifyTolerance > 0 ? TopologyPreservingSimplifier.Simplify(union, simplifyTolerance) : union;
    }
}
