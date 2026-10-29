using System.Globalization;
using System.Text.Json;
using Lookout.Drone;
using Lookout.Engine.Geo;
using Lookout.Engine.Scenarios;
using Lookout.Link;
using NetTopologySuite.Geometries;

namespace Lookout.Sim;

/// <summary>
/// drone track  &lt;video.MP4 | log.SRT&gt; [out.geojson]
/// drone survey &lt;scenario.yaml&gt; &lt;video.MP4&gt; [--srt f] [--log airdata.csv] [--heading deg] [--pitch deg]
///              [--fps n] [--hfov deg] [--model detector.onnx] [--fire-classes 0,1] [--out front.geojson]
///              [--publish incident-id]   (MQTT_HOST, MQTT_PORT, MQTT_USER, MQTT_PASSWORD)
/// drone flight &lt;scenario.yaml&gt; &lt;minute&gt; &lt;out-dir&gt; [--altitude m] [--seconds n]
/// </summary>
public static class DroneCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2) return Usage();
        var opts = Options(args);
        switch (args[1])
        {
            case "track" when args.Length >= 3:
            {
                var track = LoadTrack(args[2], opts.GetValueOrDefault("srt"));
                var outPath = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : Path.ChangeExtension(args[2], ".track.geojson");
                File.WriteAllText(outPath, JsonSerializer.Serialize(TrackGeoJson(track)));
                var span = track[^1].VideoSec - track[0].VideoSec;
                Console.WriteLine($"{track.Count} samples over {span:0} s, height {track.Min(s => s.HeightM):0}-{track.Max(s => s.HeightM):0} m above take-off. Wrote {outPath}");
                return 0;
            }
            case "survey" when args.Length >= 4:
            {
                var scenario = ScenarioLoader.LoadFile(args[2]);
                if (scenario.Terrain is null) { Console.Error.WriteLine("The scenario needs a terrain grid for ground projection."); return 2; }
                var track = LoadTrack(args[3], opts.GetValueOrDefault("srt"));
                var attitude = opts.TryGetValue("log", out var log) ? FlightLog.ParseCsv(File.ReadAllText(log)) : null;
                var camera = CameraModel.WithAspect(960, 540, D(opts, "hfov", 71.5));
                IFireDetector detector = opts.TryGetValue("model", out var model)
                    ? new OnnxFireDetector(model, fireClasses: opts.TryGetValue("fire-classes", out var fc) ? fc.Split(',').Select(int.Parse) : null)
                    : new ColorFireDetector();
                Console.WriteLine($"Detector: {detector.Name}. Pose: {(attitude is null ? $"fixed heading {D(opts, "heading", 0)}°, pitch {D(opts, "pitch", -90)}°" : $"flight log, {attitude.Count} rows")}.");

                var result = new VideoSurvey(scenario.Frame, scenario.Terrain, camera, detector)
                    .Run(args[3], track, attitude, new SurveyOptions(D(opts, "fps", 1), D(opts, "heading", 0), D(opts, "pitch", -90)));
                foreach (var f in result.Frames.Where(f => f.FirePixels > 0))
                    Console.WriteLine($"  t={f.VideoSec,5:0.0}s  {f.Lat:0.0000},{f.Lon:0.0000}  H {f.HeightM:0} m  {f.FirePixels} fire px");
                var areaHa = result.Front.Area / 10_000;
                Console.WriteLine($"{result.Frames.Count} frames, {result.BurnedCells} grid cells marked, burned area {areaHa:0.0} ha.");

                var outPath = opts.GetValueOrDefault("out") ?? Path.ChangeExtension(args[3], ".front.geojson");
                File.WriteAllText(outPath, JsonSerializer.Serialize(FrontGeoJson(scenario.Frame, result.Front, track)));
                Console.WriteLine($"Wrote {outPath}");

                if (opts.TryGetValue("publish", out var incident))
                {
                    await PublishAsync(Guid.Parse(incident), scenario.Frame, result.Front, detector.Name);
                    Console.WriteLine($"Published the front to incident {incident}.");
                }
                return 0;
            }
            case "flight" when args.Length >= 5:
            {
                var scenario = ScenarioLoader.LoadFile(args[2]);
                var minute = double.Parse(args[3], CultureInfo.InvariantCulture);
                var truth = scenario.Fire.FrontAt(minute);
                var c = truth.Centroid.Coordinate;
                var (video, srt) = SyntheticFlight.Write(args[4], scenario.Frame, scenario.Terrain!, CameraModel.WithAspect(960, 540, 71.5), truth,
                    new Coordinate(c.X, c.Y - 400), new Coordinate(c.X, c.Y + 400), D(opts, "altitude", 1400), 0, -90, (int)D(opts, "seconds", 8));
                Console.WriteLine($"Synthetic flight over the fire at minute {minute}: {video} and {srt}");
                return 0;
            }
            default:
                return Usage();
        }
    }

    private static IReadOnlyList<DroneSample> LoadTrack(string path, string? srt)
    {
        if (srt is not null) return SrtLog.ParseFile(srt);
        if (path.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)) return SrtLog.ParseFile(path);
        var sidecar = Path.ChangeExtension(path, ".SRT");
        if (File.Exists(sidecar)) return SrtLog.ParseFile(sidecar);
        return SrtLog.ParseFile(VideoSurvey.ExtractSubtitles(path, Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(path) + ".SRT")));
    }

    private static object TrackGeoJson(IReadOnlyList<DroneSample> track) => new
    {
        type = "FeatureCollection",
        features = new object[]
        {
            new
            {
                type = "Feature",
                properties = new { kind = "drone-track", samples = track.Count },
                geometry = new { type = "LineString", coordinates = track.Select(s => new[] { s.Lon, s.Lat, s.HeightM }) },
            },
        },
    };

    private static object FrontGeoJson(LocalFrame frame, Geometry front, IReadOnlyList<DroneSample> track) => new
    {
        type = "FeatureCollection",
        features = new object[]
        {
            new { type = "Feature", properties = new { kind = "burned-area", hectares = Math.Round(front.Area / 10_000, 2) },
                  geometry = new { type = "MultiPolygon", coordinates = MultiPolygon(frame, front) } },
            new { type = "Feature", properties = new { kind = "drone-track" },
                  geometry = new { type = "LineString", coordinates = track.Select(s => new[] { s.Lon, s.Lat }) } },
        },
    };

    private static double[][][][] MultiPolygon(LocalFrame frame, Geometry g) =>
        g.IsEmpty ? [] : Enumerable.Range(0, g.NumGeometries).Select(g.GetGeometryN).OfType<Polygon>()
            .Select(p => new[] { p.ExteriorRing.Coordinates.Select(c => { var (lon, lat) = frame.ToLonLat(c); return new[] { Math.Round(lon, 6), Math.Round(lat, 6) }; }).ToArray() })
            .ToArray();

    private static async Task PublishAsync(Guid incident, LocalFrame frame, Geometry front, string source)
    {
        string Env(string n, string d) => Environment.GetEnvironmentVariable(n) is { Length: > 0 } v ? v : d;
        await using var mqtt = new MqttConnection(new MqttSettings
        {
            Host = Env("MQTT_HOST", "localhost"), Port = int.Parse(Env("MQTT_PORT", "1883")),
            Username = Environment.GetEnvironmentVariable("MQTT_USER"), Password = Environment.GetEnvironmentVariable("MQTT_PASSWORD"),
        }, "lookout-drone-survey", Console.WriteLine);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await mqtt.StartAsync([], cts.Token);
        await mqtt.PublishAsync(Topics.DroneFront(incident),
            SurveyJson.Encode(new DroneFrontMessage(DateTimeOffset.UtcNow, MultiPolygon(frame, front), $"drone survey ({source})")), cts.Token);
    }

    private static Dictionary<string, string> Options(string[] args)
    {
        var d = new Dictionary<string, string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].StartsWith("--")) d[args[i][2..]] = args[i + 1];
        return d;
    }

    private static double D(Dictionary<string, string> o, string key, double fallback) =>
        o.TryGetValue(key, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static int Usage()
    {
        Console.Error.WriteLine("""
            drone track  <video.MP4 | log.SRT> [out.geojson]
            drone survey <scenario.yaml> <video.MP4> [--srt f] [--log airdata.csv] [--heading deg] [--pitch deg]
                         [--fps n] [--hfov deg] [--model detector.onnx] [--fire-classes 0,1] [--out f] [--publish incident-id]
            drone flight <scenario.yaml> <minute> <out-dir> [--altitude m] [--seconds n]
            """);
        return 2;
    }
}
