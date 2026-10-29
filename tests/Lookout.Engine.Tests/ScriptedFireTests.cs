using Lookout.Engine.Rules;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Tests;

public class ScriptedFireTests
{
    private static readonly GeometryFactory Gf = new();

    [Fact]
    public void Constant_wind_front_is_the_single_ellipse()
    {
        var c = new SpreadConditions(270, 4, 30);
        var fire = new ScriptedFire(new Coordinate(0, 0), 0, [new WindPhase(0, c)]);
        var front = fire.FrontAt(10);
        var env = front.EnvelopeInternal;
        var e = EllipseModel.Eccentricity(EllipseModel.LengthToBreadth(4));
        Assert.Equal(300.0, env.MaxX, 1);
        Assert.Equal(-EllipseModel.BackingRate(30, e) * 10, env.MinX, 1);
        Assert.True(front.Contains(Gf.CreatePoint(new Coordinate(0, 0))));
    }

    [Fact]
    public void Wind_shift_turns_the_head()
    {
        var fire = new ScriptedFire(new Coordinate(0, 0), 0,
        [
            new WindPhase(0, new SpreadConditions(0, 4, 30)),    // head south
            new WindPhase(10, new SpreadConditions(270, 4, 30)), // head east
        ]);
        var before = fire.FrontAt(10).EnvelopeInternal;
        var after = fire.FrontAt(20).EnvelopeInternal;
        Assert.Equal(-300.0, before.MinY, 1);
        Assert.True(after.MaxX > before.MaxX + 250, "head should have run about 300 m east after the shift");
        // The south edge only widens by the new ellipse's semi-minor axis b = a * sqrt(1 - e^2),
        // with a = head distance / (1 + e): about 64 m here, versus 300 m of head run.
        var e = EllipseModel.Eccentricity(EllipseModel.LengthToBreadth(4));
        var b = 300 / (1 + e) * Math.Sqrt(1 - e * e);
        Assert.Equal(before.MinY - b, after.MinY, 0);
    }

    [Fact]
    public void Nothing_burns_before_ignition()
    {
        var fire = new ScriptedFire(new Coordinate(0, 0), 5, [new WindPhase(0, new SpreadConditions(0, 4, 30))]);
        Assert.True(fire.FrontAt(5).IsEmpty);
    }

    /// <summary>
    /// Oracle check: on flat ground with constant wind, a point straight downwind of the head at
    /// distance D is reached D / R_H minutes later. Rule 1's fire ETA must agree with the truth fire.
    /// </summary>
    [Fact]
    public void Cutoff_fire_eta_agrees_with_truth_fire_on_flat_ground()
    {
        var c = new SpreadConditions(270, 5, 50);
        var fire = new ScriptedFire(new Coordinate(0, 0), 0, [new WindPhase(0, c)]);
        var front = fire.FrontAt(10); // head at x = 500
        var target = new Coordinate(1500, 0);

        // Predicted: (1500 - 500) / 50 = 20 more minutes.
        Assert.Equal(20.0, CutoffRule.PredictFireMinutes(front, target, c), 1);

        // Truth: the scripted fire first covers the target at minute 30.
        var point = Gf.CreatePoint(target);
        var truth = Enumerable.Range(0, 400).Select(i => 10 + i * 0.1).First(t => fire.FrontAt(t).Contains(point));
        Assert.InRange(truth, 29.9, 30.2);
    }

    [Fact]
    public void Crew_inside_safety_zone_is_safe()
    {
        var route = new EscapeRoute("R", "test", "Z", [new CoordinateZ(0, 0, 0), new CoordinateZ(100, 0, 0)]);
        var zone = new SafetyZone("Z", "zone", (Polygon)Gf.CreatePoint(new Coordinate(100, 0)).Buffer(30));
        var result = CutoffRule.Evaluate(new RouteProfile(route, Travel.PaceProfile.Moderate), zone,
            new Coordinate(100, 0), Gf.CreatePolygon(), new SpreadConditions(0, 3, 20), new AlertSettings(), 0);
        Assert.True(result.InSafetyZone);
        Assert.Equal(AlertLevel.None, result.Level);
    }
}
