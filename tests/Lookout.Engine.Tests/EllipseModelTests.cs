using Lookout.Engine.Spread;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Tests;

public class EllipseModelTests
{
    [Fact]
    public void Calm_wind_gives_a_circle()
    {
        Assert.Equal(1.0, EllipseModel.LengthToBreadth(0), 3);
        Assert.Equal(0.0, EllipseModel.Eccentricity(1.0), 6);
    }

    [Fact]
    public void Length_to_breadth_grows_with_wind_and_is_capped_at_8()
    {
        var lb3 = EllipseModel.LengthToBreadth(3);
        var lb6 = EllipseModel.LengthToBreadth(6);
        Assert.True(lb6 > lb3 && lb3 > 1.0);
        Assert.Equal(EllipseModel.MaxLengthToBreadth, EllipseModel.LengthToBreadth(30));
    }

    [Fact]
    public void Rate_at_head_and_back_match_the_closed_forms()
    {
        var e = EllipseModel.Eccentricity(EllipseModel.LengthToBreadth(4));
        Assert.Equal(50.0, EllipseModel.RateAtAngle(50, e, 0), 9);
        Assert.Equal(EllipseModel.BackingRate(50, e), EllipseModel.RateAtAngle(50, e, Math.PI), 9);
    }

    [Fact]
    public void Shape_head_vertex_is_head_rate_times_minutes_downwind()
    {
        // Wind from the west (270) drives the head east (+x).
        var shape = EllipseModel.Shape(new SpreadConditions(270, 5, 40), minutes: 10);
        var head = shape[0];
        Assert.Equal(400.0, head.X, 6);
        Assert.Equal(0.0, head.Y, 6);
    }

    [Fact]
    public void Travel_time_downwind_uses_head_rate_and_upwind_uses_backing_rate()
    {
        var c = new SpreadConditions(270, 5, 40);
        var e = EllipseModel.Eccentricity(EllipseModel.LengthToBreadth(5));
        var origin = new Coordinate(0, 0);
        Assert.Equal(10.0, EllipseModel.TravelMinutes(origin, new Coordinate(400, 0), c), 6);
        Assert.Equal(400 / EllipseModel.BackingRate(40, e), EllipseModel.TravelMinutes(origin, new Coordinate(-400, 0), c), 6);
    }
}
