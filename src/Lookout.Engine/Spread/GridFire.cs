using Lookout.Engine.Geo;
using NetTopologySuite.Densify;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Spread;

/// <summary>
/// The scenario's real fire on terrain: one forward minimum-travel-time run from the ignition with
/// the full weather timeline (including shifts the engine has not seen yet). The burned area at
/// minute t is every cell the fire reached by t.
/// </summary>
public sealed class GridFire : IFireTruth
{
    private readonly SpreadGrid _grid;
    private readonly double[] _arrival;
    private readonly Dictionary<double, Geometry> _fronts = new();

    public GridFire(SpreadGrid grid, Coordinate ignition, double ignitionMin, IReadOnlyList<WindPhase> phases, double horizonMin)
    {
        _grid = grid;
        var (i, j) = grid.Terrain.CellOf(ignition.X, ignition.Y);
        _arrival = grid.ForwardArrival(grid.Terrain.Index(i, j), ignitionMin, phases, horizonMin);
    }

    public IReadOnlyList<double> Arrival => _arrival;

    public Geometry FrontAt(double t)
    {
        if (_fronts.TryGetValue(t, out var cached)) return cached;
        var front = RasterPolygonizer.Polygonize(_grid.Terrain, c => _arrival[c] <= t, simplifyTolerance: _grid.Terrain.CellSize * 0.5);
        return _fronts[t] = front;
    }

    public double? FirstArrival(LineString line, double horizonMin)
    {
        var terrain = _grid.Terrain;
        var first = double.PositiveInfinity;
        foreach (var c in Densifier.Densify(line, terrain.CellSize / 2).Coordinates)
        {
            var (i, j) = terrain.CellOf(c.X, c.Y);
            first = Math.Min(first, _arrival[terrain.Index(i, j)]);
        }
        return first <= horizonMin ? Math.Round(first, 1) : null;
    }
}
