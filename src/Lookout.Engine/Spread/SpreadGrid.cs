using Lookout.Engine.Geo;
using Lookout.Engine.Terrain;

namespace Lookout.Engine.Spread;

/// <summary>
/// Minimum-travel-time fire spread on a terrain grid: the construction behind FlamMap's MTT
/// (Finney 2002) and the trigger-buffer work of Cova et al. (2005) and Fryer et al. (2013).
///
/// Each cell connects to 16 neighbours (the 8 adjacent cells plus 8 knight moves), which keeps
/// direction bias to a few percent. The fire's travel time along an edge is its length divided by
/// the elliptical spread rate in that direction (see <see cref="EllipseModel"/>), multiplied by a
/// slope factor for the rise along the edge: spread doubles for every 10 degrees of upslope
/// (McArthur 1967 rule of thumb), capped at 30 degrees, with no reduction downslope.
/// Dijkstra's algorithm then gives the earliest arrival time at every cell.
/// </summary>
public sealed class SpreadGrid
{
    private static readonly (int Dx, int Dy)[] Offsets =
    [
        (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
        (2, 1), (2, -1), (-2, 1), (-2, -1), (1, 2), (1, -2), (-1, 2), (-1, -2),
    ];

    public const double MaxSlopeDeg = 30.0;

    private readonly double[] _length;
    private readonly double[] _bearing;

    public SpreadGrid(TerrainGrid terrain)
    {
        Terrain = terrain;
        _length = Offsets.Select(o => terrain.CellSize * Math.Sqrt(o.Dx * o.Dx + o.Dy * o.Dy)).ToArray();
        _bearing = Offsets.Select(o => Bearing.Of(o.Dx, o.Dy)).ToArray();
    }

    public TerrainGrid Terrain { get; }
    public int CellCount => Terrain.Nx * Terrain.Ny;

    /// <summary>Spread multiplier for fire moving up a slope of <paramref name="rise"/> over <paramref name="run"/> metres.</summary>
    public static double SlopeFactor(double rise, double run)
    {
        if (rise <= 0) return 1.0;
        var deg = Math.Min(MaxSlopeDeg, Math.Atan2(rise, run) * 180.0 / Math.PI);
        return Math.Pow(2.0, deg / 10.0);
    }

    /// <summary>Wind-only spread rate along each of the 16 edge directions, m/min.</summary>
    private double[] DirectionalRates(SpreadConditions c)
    {
        var e = EllipseModel.Eccentricity(EllipseModel.LengthToBreadth(c.MidflameWindMs));
        var head = Bearing.HeadDirectionFromWind(c.WindFromDeg);
        return _bearing.Select(b => EllipseModel.RateAtAngle(c.HeadRateMPerMin, e, Bearing.ToRadians(Bearing.Difference(b, head)))).ToArray();
    }

    private bool TryNeighbour(int cell, int k, out int neighbour)
    {
        int i = cell % Terrain.Nx + Offsets[k].Dx, j = cell / Terrain.Nx + Offsets[k].Dy;
        neighbour = j * Terrain.Nx + i;
        return i >= 0 && i < Terrain.Nx && j >= 0 && j < Terrain.Ny;
    }

    /// <summary>
    /// Fire arrival time (minutes) at every cell, from an ignition point, under piecewise-constant
    /// weather that changes during the run. An edge crossed during a weather change uses the old
    /// rate until the change and the new rate after it, so arrival is never earlier for a later
    /// departure and Dijkstra stays exact. Cells not reached by <paramref name="horizonMin"/> are +infinity.
    /// </summary>
    public double[] ForwardArrival(int ignitionCell, double ignitionMin, IReadOnlyList<WindPhase> phases, double horizonMin)
    {
        var rates = phases.Select(p => DirectionalRates(p.Conditions)).ToArray();
        var starts = phases.Select(p => p.StartMin).ToArray();
        var arrival = Filled(double.PositiveInfinity);
        var queue = new PriorityQueue<int, double>();
        arrival[ignitionCell] = ignitionMin;
        queue.Enqueue(ignitionCell, ignitionMin);

        while (queue.TryDequeue(out var a, out var ta))
        {
            if (ta > arrival[a] || ta > horizonMin) continue;
            for (var k = 0; k < Offsets.Length; k++)
            {
                if (!TryNeighbour(a, k, out var b)) continue;
                var slope = SlopeFactor(Terrain.Z[b] - Terrain.Z[a], _length[k]);
                var tb = Traverse(ta, _length[k], slope, k, rates, starts);
                if (tb < arrival[b]) { arrival[b] = tb; queue.Enqueue(b, tb); }
            }
        }
        return arrival;
    }

    private static double Traverse(double departMin, double length, double slope, int k, double[][] rates, double[] starts)
    {
        var p = 0;
        while (p + 1 < starts.Length && starts[p + 1] <= departMin) p++;
        double t = departMin, remaining = length;
        while (true)
        {
            var rate = rates[p][k] * slope;
            var end = p + 1 < starts.Length ? starts[p + 1] : double.PositiveInfinity;
            var need = remaining / rate;
            if (t + need <= end) return t + need;
            remaining -= (end - t) * rate;
            t = end;
            p++;
        }
    }

    /// <summary>
    /// Reverse run under constant wind. Each seed is a route cell labelled with minus the crew's
    /// minutes to reach it. The result at a cell c is
    ///     min over seeds p of (fire minutes from c to p - crew minutes to p),
    /// the safety margin the crew would have if the fire front were at c right now. Searching
    /// stops at <paramref name="horizonMin"/>; cells beyond it stay +infinity.
    /// </summary>
    public ReverseResult ReverseMargin(IEnumerable<(int Cell, double Label, int Source)> seeds, SpreadConditions wind, double horizonMin)
    {
        var rates = DirectionalRates(wind);
        var label = Filled(double.PositiveInfinity);
        var source = new int[CellCount];
        var queue = new PriorityQueue<int, double>();
        foreach (var (cell, l, s) in seeds)
        {
            if (l < label[cell]) { label[cell] = l; source[cell] = s; queue.Enqueue(cell, l); }
        }

        while (queue.TryDequeue(out var v, out var lv))
        {
            if (lv > label[v] || lv > horizonMin) continue;
            // Relax every u whose edge u -> v (offset k) reaches v: fire would travel from u to v.
            for (var k = 0; k < Offsets.Length; k++)
            {
                int i = v % Terrain.Nx - Offsets[k].Dx, j = v / Terrain.Nx - Offsets[k].Dy;
                if (i < 0 || i >= Terrain.Nx || j < 0 || j >= Terrain.Ny) continue;
                var u = j * Terrain.Nx + i;
                var cost = _length[k] / (rates[k] * SlopeFactor(Terrain.Z[v] - Terrain.Z[u], _length[k]));
                var lu = lv + cost;
                if (lu < label[u]) { label[u] = lu; source[u] = source[v]; queue.Enqueue(u, lu); }
            }
        }
        return new ReverseResult(label, source);
    }

    private double[] Filled(double value)
    {
        var a = new double[CellCount];
        Array.Fill(a, value);
        return a;
    }
}

/// <param name="Margin">Per cell: crew safety margin in minutes if the fire front were there now.</param>
/// <param name="Source">Per cell: index of the route seed that sets that margin (the cut point).</param>
public sealed record ReverseResult(double[] Margin, int[] Source);
