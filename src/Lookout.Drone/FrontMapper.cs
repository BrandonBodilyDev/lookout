using Lookout.Engine.Geo;
using Lookout.Engine.Terrain;
using NetTopologySuite.Geometries;

namespace Lookout.Drone;

/// <summary>
/// Turns detection masks into a burned-area polygon on the scenario's terrain grid. Each frame's
/// fire pixels are projected to the ground and mark grid cells; marks accumulate across frames
/// (burned ground stays burned). A cell needs at least two fire pixels per frame to count, which
/// drops single-pixel noise. Single-cell gaps inside the mark are filled before polygonizing.
/// </summary>
public sealed class FrontMapper(TerrainGrid grid, GroundProjector projector, int pixelStride = 2)
{
    private readonly bool[] _burned = new bool[grid.Nx * grid.Ny];

    public int BurnedCells => _burned.Count(b => b);

    public void Add(FireMask mask, CameraPose pose)
    {
        var hits = new Dictionary<int, int>();
        for (var v = 0; v < mask.Height; v += pixelStride)
        for (var u = 0; u < mask.Width; u += pixelStride)
        {
            if (!mask[u, v]) continue;
            if (projector.PixelToGround(u + 0.5, v + 0.5, pose) is not { } g) continue;
            var (i, j) = grid.CellOf(g.X, g.Y);
            if (g.X < grid.X0 || g.Y < grid.Y0 || i >= grid.Nx || j >= grid.Ny) continue;
            var cell = grid.Index(i, j);
            hits[cell] = hits.GetValueOrDefault(cell) + 1;
        }
        foreach (var (cell, n) in hits)
            if (n >= 2) _burned[cell] = true;
    }

    public Geometry Front()
    {
        var filled = (bool[])_burned.Clone();
        for (var j = 1; j < grid.Ny - 1; j++)
        for (var i = 1; i < grid.Nx - 1; i++)
        {
            var c = grid.Index(i, j);
            if (filled[c]) continue;
            var around = (_burned[c - 1] ? 1 : 0) + (_burned[c + 1] ? 1 : 0) + (_burned[c - grid.Nx] ? 1 : 0) + (_burned[c + grid.Nx] ? 1 : 0);
            if (around >= 3) filled[c] = true;
        }
        return RasterPolygonizer.Polygonize(grid, c => filled[c], simplifyTolerance: grid.CellSize * 0.5);
    }
}
