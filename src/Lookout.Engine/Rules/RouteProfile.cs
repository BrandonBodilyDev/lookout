using Lookout.Engine.Scenarios;
using Lookout.Engine.Travel;
using NetTopologySuite.Geometries;
using NetTopologySuite.LinearReferencing;

namespace Lookout.Engine.Rules;

/// <summary>
/// An escape route resampled every few metres, with ground elevation and the cumulative crew
/// travel time from the route start at each sample (slope-adjusted, walking toward the safety zone).
/// </summary>
public sealed class RouteProfile
{
    public const double SampleSpacingM = 20.0;

    private readonly LengthIndexedLine _indexed;

    public RouteProfile(EscapeRoute route, PaceProfile pace)
    {
        Route = route;
        Pace = pace;
        _indexed = new LengthIndexedLine(route.Line);

        var samples = new List<Sample>();
        var cumulativeS = new double[route.Vertices.Count];
        for (var i = 1; i < route.Vertices.Count; i++)
            cumulativeS[i] = cumulativeS[i - 1] + route.Vertices[i - 1].Distance(route.Vertices[i]);

        double travel = 0;
        Sample? prev = null;
        for (var i = 0; i < route.Vertices.Count - 1; i++)
        {
            var a = route.Vertices[i];
            var b = route.Vertices[i + 1];
            var len = cumulativeS[i + 1] - cumulativeS[i];
            var steps = Math.Max(1, (int)Math.Ceiling(len / SampleSpacingM));
            for (var k = i == 0 ? 0 : 1; k <= steps; k++)
            {
                var f = (double)k / steps;
                var p = new Coordinate(a.X + f * (b.X - a.X), a.Y + f * (b.Y - a.Y));
                var z = a.Z + f * (b.Z - a.Z);
                var s = cumulativeS[i] + f * len;
                if (prev is not null)
                    travel += TravelRate.SegmentMinutes(s - prev.S, z - prev.Z, pace);
                prev = new Sample(p, s, z, travel);
                samples.Add(prev);
            }
        }
        Samples = samples;
        LengthM = cumulativeS[^1];
    }

    public EscapeRoute Route { get; }
    public PaceProfile Pace { get; }
    public IReadOnlyList<Sample> Samples { get; }
    public double LengthM { get; }

    /// <summary>Distance along the route of the point nearest <paramref name="p"/>.</summary>
    public double Project(Coordinate p) => Math.Clamp(_indexed.Project(p), 0, LengthM);

    public Coordinate PointAt(double s) => _indexed.ExtractPoint(s);

    /// <summary>Cumulative crew minutes from the route start to distance <paramref name="s"/> (interpolated).</summary>
    public double TravelMinutesTo(double s)
    {
        if (s <= 0) return 0;
        for (var i = 1; i < Samples.Count; i++)
        {
            if (Samples[i].S >= s)
            {
                var a = Samples[i - 1];
                var b = Samples[i];
                var f = (s - a.S) / (b.S - a.S);
                return a.TravelMin + f * (b.TravelMin - a.TravelMin);
            }
        }
        return Samples[^1].TravelMin;
    }

    /// <param name="Point">Location in the local frame.</param>
    /// <param name="S">Horizontal distance along the route from its start, metres.</param>
    /// <param name="Z">Ground elevation, metres.</param>
    /// <param name="TravelMin">Crew minutes from the route start to here.</param>
    public sealed record Sample(Coordinate Point, double S, double Z, double TravelMin);
}
