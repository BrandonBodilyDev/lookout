using Lookout.Engine.Rules;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using Lookout.Engine.Travel;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Tests;

/// <summary>
/// Regression for a review finding: when the best route flipped but the alert level stayed the same,
/// no message was sent, so the crew's last instruction could name a route that was now cut.
/// Two routes from the origin: ER-A east, ER-B west. The fire first threatens ER-B, then ER-A.
/// </summary>
public class RerouteTests
{
    private static readonly GeometryFactory Gf = new();

    private static Polygon Box(double x, double y, double h) => Gf.CreatePolygon([
        new(x - h, y - h), new(x + h, y - h), new(x + h, y + h), new(x - h, y + h), new(x - h, y - h)]);

    private static (CrewMonitor Monitor, List<RouteOption> Routes) Setup()
    {
        var a = new EscapeRoute("ER-A", "east", "SZ-A", [new CoordinateZ(0, 0, 0), new CoordinateZ(1100, 0, 0)]);
        var b = new EscapeRoute("ER-B", "west", "SZ-B", [new CoordinateZ(0, 0, 0), new CoordinateZ(-1100, 0, 0)]);
        return (new CrewMonitor("ALPHA", new AlertSettings(), null),
        [
            new(new RouteProfile(a, PaceProfile.Moderate), new SafetyZone("SZ-A", "A", Box(1100, 0, 100))),
            new(new RouteProfile(b, PaceProfile.Moderate), new SafetyZone("SZ-B", "B", Box(-1100, 0, 100))),
        ]);
    }

    [Fact]
    public void A_change_of_best_route_at_the_same_level_is_sent()
    {
        var (monitor, routes) = Setup();
        var wind = new SpreadConditions(0, 0.1, 20);
        var messages = new List<string>();
        var t = 0.0;
        foreach (var (fx, fy) in new[] { (-600.0, 250.0), (-600, 250), (600, 250), (600, 250), (900, 100), (900, 100) })
        {
            var a = monitor.Assess(routes, new Coordinate(0, 0), t, Box(fx, fy, 50), wind, t, m => $"{m:0}");
            if (a.Change is { } c) messages.Add(c.Text);
            t += 0.5;
        }

        Assert.Contains(messages, m => m.Contains("via ER-A") && m.Contains("Avoid ER-B"));
        // Once the fire moves east, the crew must hear about ER-B, and must be told to avoid ER-A.
        var reroute = Assert.Single(messages, m => m.Contains("via ER-B"));
        Assert.Contains("Avoid ER-A", reroute);
        Assert.True(messages.IndexOf(reroute) > messages.FindIndex(m => m.Contains("via ER-A")));
    }

    [Fact]
    public void Similar_margins_do_not_flip_the_recommendation()
    {
        var (monitor, routes) = Setup();
        var wind = new SpreadConditions(0, 0.1, 20);
        var told = new List<string>();
        var t = 0.0;
        // The fire sits almost due north: both routes have nearly equal margins that wobble a little.
        foreach (var fx in new[] { -10.0, 10, -10, 10, -10, 10 })
        {
            var a = monitor.Assess(routes, new Coordinate(0, 0), t, Box(fx, 300, 50), wind, t, m => $"{m:0}");
            told.Add(a.RouteId);
            t += 0.5;
        }
        Assert.Single(told.Distinct());
    }
}
