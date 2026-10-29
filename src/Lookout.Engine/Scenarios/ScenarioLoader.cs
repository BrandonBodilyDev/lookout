using System.Globalization;
using Lookout.Engine.Geo;
using Lookout.Engine.Spread;
using Lookout.Engine.Terrain;
using Lookout.Engine.Travel;
using NetTopologySuite.Geometries;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lookout.Engine.Scenarios;

/// <summary>Reads a scenario YAML file (lon/lat, minutes) and builds a <see cref="Scenario"/> in the local metric frame.</summary>
public static class ScenarioLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <param name="loadTerrain">False reads the scenario without its terrain grid (used to build that grid).</param>
    public static Scenario LoadFile(string path, bool loadTerrain = true) =>
        Parse(File.ReadAllText(path), loadTerrain ? Path.GetDirectoryName(Path.GetFullPath(path)) : null);

    /// <param name="baseDir">Folder that relative terrain paths resolve against; null skips terrain.</param>
    public static Scenario Parse(string yaml, string? baseDir = null)
    {
        var f = Yaml.Deserialize<ScenarioFile>(yaml)
            ?? throw new InvalidDataException("Scenario file is empty.");
        var frame = new LocalFrame(f.Origin.Lon, f.Origin.Lat);
        Coordinate Local(IList<double> lonLat) => frame.ToLocal(lonLat[0], lonLat[1]);

        var wind = f.Wind
            .Select(w => new WindPhase(w.StartMin, new SpreadConditions(w.FromDeg, w.MidflameWindMs, w.HeadRateMPerMin)))
            .OrderBy(w => w.StartMin)
            .ToList();
        if (wind.Count == 0) throw new InvalidDataException("Scenario needs at least one wind phase.");

        var gf = new GeometryFactory();
        var zones = f.SafetyZones.Select(z =>
        {
            var ring = z.Polygon.Select(Local).ToList();
            if (!ring[0].Equals2D(ring[^1])) ring.Add(ring[0].Copy());
            return new SafetyZone(z.Id, z.Name, gf.CreatePolygon(ring.ToArray()));
        }).ToList();

        var terrain = f.Terrain is { } tf && baseDir is not null
            ? TerrainGrid.ReadAscii(Path.Combine(baseDir, tf.File))
            : null;

        // A vertex may omit its elevation; then it comes from the terrain grid (or 0 on flat scenarios).
        var routes = f.Routes.Select(r => new EscapeRoute(
            r.Id, r.Name, r.SafetyZone,
            r.Vertices.Select(v =>
            {
                var c = Local(v);
                var z = v.Count >= 3 ? v[2] : terrain?.Elevation(c.X, c.Y) ?? 0;
                return new CoordinateZ(c.X, c.Y, Math.Round(z, 1));
            }).ToList()))
            .ToList();

        foreach (var r in routes)
        {
            var zone = zones.SingleOrDefault(z => z.Id == r.SafetyZoneId)
                ?? throw new InvalidDataException($"Route {r.Id} names unknown safety zone {r.SafetyZoneId}.");
            if (!zone.Area.Contains(gf.CreatePoint(r.Vertices[^1])))
                throw new InvalidDataException($"Route {r.Id} must end inside safety zone {zone.Id}.");
        }

        var crews = f.Crews.Select(c =>
        {
            var routeIds = c.Routes.Count > 0 ? c.Routes : [c.Route];
            if (routeIds.Distinct().Count() != routeIds.Count)
                throw new InvalidDataException($"Crew {c.Id} lists the same escape route twice.");
            foreach (var id in routeIds)
                if (routes.All(r => r.Id != id))
                    throw new InvalidDataException($"Crew {c.Id} names unknown route {id}.");
            var track = c.Track.Select(k => (k[0], frame.ToLocal(k[1], k[2]))).OrderBy(k => k.Item1).ToList();
            return new Crew
            {
                Id = c.Id,
                Name = c.Name,
                RouteId = routeIds[0],
                AlternateRouteIds = routeIds.Skip(1).ToList(),
                Pace = Enum.Parse<PaceProfile>(c.Pace, ignoreCase: true),
                Size = c.Size,
                Track = track,
                Fixes = SampleFixes(track, c.FixIntervalS / 60.0, c.CommsGaps, f.DurationMin),
                CommsGaps = c.CommsGaps.Select(g => (g[0], g[1])).ToList(),
            };
        }).ToList();

        return new Scenario
        {
            Id = f.Id,
            Name = f.Name,
            Description = f.Description.Trim(),
            Start = DateTimeOffset.Parse(f.Start, CultureInfo.InvariantCulture),
            DurationMin = f.DurationMin,
            StepMin = f.StepMin,
            Frame = frame,
            Fire = terrain is null
                ? new ScriptedFire(Local(f.Fire.Ignition), f.Fire.IgnitionMin, wind)
                : new GridFire(new SpreadGrid(terrain), Local(f.Fire.Ignition), f.Fire.IgnitionMin, wind, f.DurationMin),
            Wind = wind,
            Routes = routes,
            SafetyZones = zones,
            Crews = crews,
            Alerts = f.Alerts ?? new AlertSettings(),
            Terrain = terrain,
        };
    }

    /// <summary>Samples the true track at the fix interval, dropping fixes that fall inside a comms gap.</summary>
    private static List<(double, Coordinate)> SampleFixes(
        List<(double T, Coordinate P)> track, double intervalMin, List<List<double>> gaps, double durationMin)
    {
        var fixes = new List<(double, Coordinate)>();
        for (var t = 0.0; t <= durationMin + 1e-9; t += intervalMin)
        {
            var inGap = gaps.Any(g => t > g[0] && t < g[1]);
            if (!inGap) fixes.Add((t, Crew.Interpolate(track, t)));
        }
        return fixes;
    }

    // ---- YAML shape (snake_case in the file) ----
    private sealed class ScenarioFile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Start { get; set; } = "";
        public double DurationMin { get; set; }
        public double StepMin { get; set; } = 0.5;
        public LonLat Origin { get; set; } = new();
        public FireFile Fire { get; set; } = new();
        public List<WindFile> Wind { get; set; } = [];
        public List<ZoneFile> SafetyZones { get; set; } = [];
        public List<RouteFile> Routes { get; set; } = [];
        public List<CrewFile> Crews { get; set; } = [];
        public AlertSettings? Alerts { get; set; }
        public TerrainFile? Terrain { get; set; }
    }

    private sealed class TerrainFile
    {
        public string File { get; set; } = "";
        public string Source { get; set; } = "";
    }

    private sealed class LonLat
    {
        public double Lon { get; set; }
        public double Lat { get; set; }
    }

    private sealed class FireFile
    {
        public List<double> Ignition { get; set; } = [];
        public double IgnitionMin { get; set; }
    }

    private sealed class WindFile
    {
        public double StartMin { get; set; }
        public double FromDeg { get; set; }
        public double MidflameWindMs { get; set; }
        public double HeadRateMPerMin { get; set; }
    }

    private sealed class ZoneFile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<List<double>> Polygon { get; set; } = [];
    }

    private sealed class RouteFile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string SafetyZone { get; set; } = "";
        public List<List<double>> Vertices { get; set; } = [];
    }

    private sealed class CrewFile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>A single escape route...</summary>
        public string Route { get; set; } = "";
        /// <summary>...or several, primary first.</summary>
        public List<string> Routes { get; set; } = [];
        public string Pace { get; set; } = "moderate";
        public int Size { get; set; } = 20;
        public double FixIntervalS { get; set; } = 60;
        public List<List<double>> Track { get; set; } = [];
        public List<List<double>> CommsGaps { get; set; } = [];
    }
}
