using Lookout.Engine.Rules;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using Lookout.Engine.Terrain;
using Lookout.Engine.Travel;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Tests;

/// <summary>The grid engine must agree with the closed-form ellipse where both apply (flat, uniform ground).</summary>
public class SpreadGridTests
{
    private static readonly GeometryFactory Gf = new();
    private static readonly Envelope Area = new(-1500, 1500, -1500, 1500);

    private static (SpreadGrid Grid, double[] Arrival) Run(TerrainGrid terrain, params WindPhase[] phases)
    {
        var grid = new SpreadGrid(terrain);
        var (i, j) = terrain.CellOf(0, 0);
        return (grid, grid.ForwardArrival(terrain.Index(i, j), 0, phases, 1000));
    }

    private static double ArrivalAt(SpreadGrid g, double[] arrival, double x, double y)
    {
        var (i, j) = g.Terrain.CellOf(x, y);
        return arrival[g.Terrain.Index(i, j)];
    }

    [Fact]
    public void Flat_ground_matches_the_ellipse_downwind_and_crosswind()
    {
        var wind = new SpreadConditions(270, 5, 30);
        var (g, arrival) = Run(TerrainGrid.Flat(Area, 30), new WindPhase(0, wind));
        var origin = new Coordinate(15, 15); // centre of the ignition cell

        foreach (var target in new[] { new Coordinate(915, 15), new Coordinate(15, 615), new Coordinate(615, 615), new Coordinate(-585, 15) })
        {
            var expected = EllipseModel.TravelMinutes(origin, target, wind);
            var actual = ArrivalAt(g, arrival, target.X, target.Y);
            Assert.InRange(actual / expected, 0.97, 1.06);
        }
    }

    [Fact]
    public void Ten_degree_upslope_doubles_the_head_rate()
    {
        var flat = TerrainGrid.Flat(Area, 30);
        var rise = Math.Tan(10 * Math.PI / 180) * 30; // per cell, rising to the east
        var z = new double[flat.Nx * flat.Ny];
        for (var j = 0; j < flat.Ny; j++)
            for (var i = 0; i < flat.Nx; i++) z[j * flat.Nx + i] = i * rise;
        var sloped = new TerrainGrid(flat.Nx, flat.Ny, flat.X0, flat.Y0, 30, z);

        var wind = new SpreadConditions(270, 5, 30);
        var (gf, af) = Run(flat, new WindPhase(0, wind));
        var (gs, aslope) = Run(sloped, new WindPhase(0, wind));
        Assert.Equal(0.5, ArrivalAt(gs, aslope, 615, 15) / ArrivalAt(gf, af, 615, 15), 2);
    }

    [Fact]
    public void Wind_change_mid_edge_uses_old_rate_then_new_rate()
    {
        // 30 m/min east for 10 minutes (300 m), then 60 m/min: 600 m east is reached at minute 15.
        var (g, arrival) = Run(TerrainGrid.Flat(Area, 30),
            new WindPhase(0, new SpreadConditions(270, 5, 30)),
            new WindPhase(10, new SpreadConditions(270, 5, 60)));
        Assert.Equal(15.0, ArrivalAt(g, arrival, 615, 15), 1);
    }

    [Fact]
    public void Reverse_margin_matches_the_ellipse_rule_on_flat_ground()
    {
        var terrain = TerrainGrid.Flat(Area, 30, elevation: 1000);
        var grid = new SpreadGrid(terrain);
        var wind = new SpreadConditions(270, 5, 40);
        var front = new ScriptedFire(new Coordinate(-900, 0), 0, [new WindPhase(0, wind)]).FrontAt(5);

        var route = new EscapeRoute("R", "test", "Z", [new CoordinateZ(300, -300, 1000), new CoordinateZ(300, 900, 1000)]);
        var zone = new SafetyZone("Z", "zone", (Polygon)Gf.CreatePoint(new Coordinate(300, 900)).Buffer(60));
        var profile = new RouteProfile(route, PaceProfile.Moderate);
        var settings = new AlertSettings();
        var crew = new Coordinate(300, -300);

        var ellipse = CutoffRule.Evaluate(profile, zone, crew, front, wind, settings, 5);
        var gridResult = GridCutoffRule.Evaluate(grid, profile, zone, crew, front, wind, settings, 5);

        Assert.InRange(gridResult.MarginMin - ellipse.MarginMin, -1.5, 1.5);
        Assert.Equal(ellipse.Level, gridResult.Level);
        Assert.NotNull(gridResult.TriggerZone);
        Assert.False(gridResult.TriggerZone!.IsEmpty);
    }
}
