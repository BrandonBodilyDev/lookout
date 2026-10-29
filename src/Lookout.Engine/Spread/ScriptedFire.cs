using NetTopologySuite.Geometries;

namespace Lookout.Engine.Spread;

/// <summary>
/// The scenario's "true" fire, which stands in for what the drone would observe. Grows from an
/// ignition point under piecewise-constant wind on flat, uniform fuel.
///
/// Within one wind phase, Huygens' principle says every point on the front ignites its own
/// ellipse, so the new front is the Minkowski sum of the old front and the phase's ellipse.
/// Both shapes are convex, so the sum is the convex hull of all pairwise vertex sums: exact for
/// this model and free of the self-intersection problems of vertex-marching wavelet codes.
/// Terrain and fuel variation are out of scope for Phase 0 (they arrive with the grid model).
/// </summary>
public sealed class ScriptedFire : IFireTruth
{
    private static readonly GeometryFactory Factory = new();
    private readonly Coordinate _ignition;
    private readonly double _ignitionMin;
    private readonly IReadOnlyList<WindPhase> _phases;
    private readonly int _vertexCount;
    private readonly Dictionary<int, Geometry> _boundaryFronts = new();

    public ScriptedFire(Coordinate ignition, double ignitionMin, IReadOnlyList<WindPhase> phases, int vertexCount = 72)
    {
        if (phases.Count == 0) throw new ArgumentException("At least one wind phase is required.", nameof(phases));
        _ignition = ignition;
        _ignitionMin = ignitionMin;
        _phases = phases.OrderBy(p => p.StartMin).ToList();
        _vertexCount = vertexCount;
    }

    /// <summary>Burned area at time <paramref name="t"/> (minutes since scenario start), or empty before ignition.</summary>
    public Geometry FrontAt(double t)
    {
        if (t <= _ignitionMin) return Factory.CreatePolygon();

        Geometry front = Factory.CreatePoint(_ignition);
        for (var i = 0; i < _phases.Count; i++)
        {
            var start = Math.Max(_phases[i].StartMin, _ignitionMin);
            var end = i + 1 < _phases.Count ? _phases[i + 1].StartMin : double.PositiveInfinity;
            if (t <= start) break;

            if (t >= end && _boundaryFronts.TryGetValue(i, out var cached))
            {
                front = cached;
                continue;
            }

            var minutes = Math.Min(t, end) - start;
            if (minutes > 0)
                front = Grow(front, EllipseModel.Shape(_phases[i].Conditions, minutes, _vertexCount));
            if (t >= end) _boundaryFronts[i] = front;
        }
        return front;
    }

    public double? FirstArrival(LineString line, double horizonMin)
    {
        for (var t = 0.0; t <= horizonMin + 1e-9; t += 0.1)
        {
            var front = FrontAt(t);
            if (!front.IsEmpty && front.Intersects(line)) return Math.Round(t, 1);
        }
        return null;
    }

    private static Geometry Grow(Geometry front, Coordinate[] brush)
    {
        var sums = new List<Coordinate>(front.NumPoints * brush.Length);
        foreach (var f in front.Coordinates)
            foreach (var b in brush)
                sums.Add(new Coordinate(f.X + b.X, f.Y + b.Y));
        return Factory.CreateMultiPointFromCoords(sums.ToArray()).ConvexHull();
    }
}
