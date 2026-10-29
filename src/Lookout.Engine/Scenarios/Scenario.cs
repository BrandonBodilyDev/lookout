using Lookout.Engine.Geo;
using Lookout.Engine.Spread;
using Lookout.Engine.Terrain;
using Lookout.Engine.Travel;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Scenarios;

/// <summary>A loaded scenario in the local metric frame. Built by <see cref="ScenarioLoader"/>.</summary>
public sealed class Scenario
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset Start { get; init; }
    public required double DurationMin { get; init; }
    public required double StepMin { get; init; }
    public required LocalFrame Frame { get; init; }
    public required IFireTruth Fire { get; init; }
    public required IReadOnlyList<WindPhase> Wind { get; init; }
    public required IReadOnlyList<EscapeRoute> Routes { get; init; }
    public required IReadOnlyList<SafetyZone> SafetyZones { get; init; }
    public required IReadOnlyList<Crew> Crews { get; init; }
    public required AlertSettings Alerts { get; init; }
    /// <summary>Ground elevation; null for flat-ground scenarios (Phase 0 ellipse engine).</summary>
    public TerrainGrid? Terrain { get; init; }

    public EscapeRoute Route(string id) => Routes.Single(r => r.Id == id);
    public SafetyZone SafetyZone(string id) => SafetyZones.Single(z => z.Id == id);
}

public sealed record SafetyZone(string Id, string Name, Polygon Area);

/// <summary>An escape route as drawn by the IC: a polyline with ground elevation at each vertex, ending in a safety zone.</summary>
public sealed class EscapeRoute
{
    public EscapeRoute(string id, string name, string safetyZoneId, IReadOnlyList<CoordinateZ> vertices)
    {
        if (vertices.Count < 2) throw new ArgumentException("A route needs at least two vertices.", nameof(vertices));
        Id = id;
        Name = name;
        SafetyZoneId = safetyZoneId;
        Vertices = vertices;
        Line = new GeometryFactory().CreateLineString(vertices.Select(v => new Coordinate(v.X, v.Y)).ToArray());
    }

    public string Id { get; }
    public string Name { get; }
    public string SafetyZoneId { get; }
    public IReadOnlyList<CoordinateZ> Vertices { get; }
    public LineString Line { get; }
}

public sealed class Crew
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string RouteId { get; init; }
    public required PaceProfile Pace { get; init; }
    public required int Size { get; init; }
    /// <summary>Where the crew actually is (scenario truth), as time-stamped keyframes.</summary>
    public required IReadOnlyList<(double T, Coordinate Position)> Track { get; init; }
    /// <summary>The GPS fixes that actually reach the server (track sampled at the fix interval, minus comms gaps).</summary>
    public required IReadOnlyList<(double T, Coordinate Position)> Fixes { get; init; }
    /// <summary>Scenario minutes when the crew's device is out of contact, both directions.</summary>
    public IReadOnlyList<(double From, double To)> CommsGaps { get; init; } = [];

    public bool InCommsGap(double t) => CommsGaps.Any(g => t > g.From && t < g.To);

    public Coordinate TruePositionAt(double t) => Interpolate(Track, t);

    /// <summary>The latest fix received at or before <paramref name="t"/>, or null if none yet.</summary>
    public (double T, Coordinate Position)? LastFixAt(double t)
    {
        (double, Coordinate)? last = null;
        foreach (var f in Fixes)
        {
            if (f.T <= t) last = f;
            else break;
        }
        return last;
    }

    internal static Coordinate Interpolate(IReadOnlyList<(double T, Coordinate Position)> keys, double t)
    {
        if (t <= keys[0].T) return keys[0].Position;
        for (var i = 1; i < keys.Count; i++)
        {
            if (t <= keys[i].T)
            {
                var (t0, p0) = keys[i - 1];
                var (t1, p1) = keys[i];
                var f = (t - t0) / (t1 - t0);
                return new Coordinate(p0.X + f * (p1.X - p0.X), p0.Y + f * (p1.Y - p0.Y));
            }
        }
        return keys[^1].Position;
    }
}

/// <summary>Rule thresholds. Defaults and sources are in docs/assumptions.md.</summary>
public sealed record AlertSettings
{
    public double BufferMin { get; init; } = 10;
    public double WatchMin { get; init; } = 30;
    public double WarningMin { get; init; } = 15;
    public double CriticalMin { get; init; } = 5;
    public double CommsStaleMin { get; init; } = 5;
    public double CommsLostMin { get; init; } = 15;
    public double UnburnedFuelDistanceM { get; init; } = 500;
    public double UnburnedFuelHalfAngleDeg { get; init; } = 45;
    public double OffTrailFactor { get; init; } = 0.5;
    /// <summary>A lower alert level must hold this long before the downgrade is sent.</summary>
    public double DowngradeHoldMin { get; init; } = 2;
}
