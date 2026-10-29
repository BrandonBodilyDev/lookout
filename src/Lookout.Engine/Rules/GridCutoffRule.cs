using Lookout.Engine.Geo;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace Lookout.Engine.Rules;

/// <summary>
/// Rule 1 on terrain. Same question as <see cref="CutoffRule"/> (fire arrival minus crew arrival,
/// minimised over the remaining route), answered with one reverse minimum-travel-time run seeded
/// from the crew's remaining route. That single run gives the margin from wherever the front is,
/// and a "leave-now zone": every cell from which a fire front would leave the crew no more than
/// the buffer. When the front touches that zone, the crew has to move.
/// </summary>
public static class GridCutoffRule
{
    /// <summary>Margins beyond this many minutes past the watch threshold cannot change the alert level.</summary>
    private const double SearchSlackMin = 30;

    public static CutoffResult Evaluate(
        SpreadGrid grid, RouteProfile profile, SafetyZone zone, Coordinate crewPosition, Geometry front,
        SpreadConditions wind, AlertSettings settings, double nowMin)
    {
        if (zone.Area.Contains(zone.Area.Factory.CreatePoint(crewPosition)))
            return CutoffResult.Safe(zone.Id);

        var terrain = grid.Terrain;
        var route = CutoffRule.RemainingRoute(profile, crewPosition, settings);
        var seeds = route.Select((r, idx) =>
        {
            var (i, j) = terrain.CellOf(r.P.X, r.P.Y);
            return (terrain.Index(i, j), -r.CrewMin, idx);
        });
        var horizon = settings.BufferMin + settings.WatchMin + SearchSlackMin;
        var reverse = grid.ReverseMargin(seeds, wind, horizon);

        // Margin from the current front: the smallest label among burned cells.
        var margin = double.PositiveInfinity;
        var best = -1;
        foreach (var cell in FrontCells(terrain, front))
        {
            if (reverse.Margin[cell] < margin) { margin = reverse.Margin[cell]; best = cell; }
        }

        var cutIdx = best >= 0 ? reverse.Source[best] : route.Count - 1;
        var (cut, crewAtCut) = route[cutIdx];
        var triggerZone = RasterPolygonizer.Polygonize(
            terrain, c => reverse.Margin[c] <= settings.BufferMin, simplifyTolerance: terrain.CellSize * 0.5);

        return CutoffRule.Result(zone, settings, nowMin, margin, cut, margin + crewAtCut, crewAtCut,
            route[^1].CrewMin, triggerZone);
    }

    /// <summary>Cells whose centres lie inside the reported front (at least the cell under its centroid).</summary>
    private static IEnumerable<int> FrontCells(Terrain.TerrainGrid terrain, Geometry front)
    {
        if (front.IsEmpty) yield break;
        var prepared = PreparedGeometryFactory.Prepare(front);
        var env = front.EnvelopeInternal;
        var (i0, j0) = terrain.CellOf(env.MinX, env.MinY);
        var (i1, j1) = terrain.CellOf(env.MaxX, env.MaxY);
        var any = false;
        for (var j = j0; j <= j1; j++)
        for (var i = i0; i <= i1; i++)
        {
            if (prepared.Contains(front.Factory.CreatePoint(terrain.Centre(i, j))))
            {
                any = true;
                yield return terrain.Index(i, j);
            }
        }
        if (!any)
        {
            var c = front.Centroid.Coordinate;
            var (ci, cj) = terrain.CellOf(c.X, c.Y);
            yield return terrain.Index(ci, cj);
        }
    }
}
