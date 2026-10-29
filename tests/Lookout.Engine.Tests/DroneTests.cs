using Lookout.Drone;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Terrain;
using NetTopologySuite.Geometries;

namespace Lookout.Engine.Tests;

public class SrtLogTests
{
    // Verbatim cues from a Mavic Mini (DJI_SRT_Parser sample): longitude comes first.
    private const string MavicMini = """
        1
        00:00:00,000 --> 00:00:01,000
        F/2.8, SS 30.15, ISO 260, EV -1.7, GPS (-121.7458, 48.0771, 17), D 221.21m, H 200.70m, H.S n/a, V.S n/a

        2
        00:00:01,000 --> 00:00:02,000
        F/2.8, SS 30.15, ISO 260, EV -1.7, GPS (-121.7460, 48.0773, 17), D 221.18m, H 202.70m, H.S n/a, V.S n/a
        """;

    [Fact]
    public void Parses_mavic_mini_cues_longitude_first()
    {
        var s = SrtLog.Parse(MavicMini);
        Assert.Equal(2, s.Count);
        Assert.Equal(48.0771, s[0].Lat, 4);
        Assert.Equal(-121.7458, s[0].Lon, 4);
        Assert.Equal(200.70, s[0].HeightM, 2);
        Assert.Equal(221.21, s[0].DistanceFromHomeM!.Value, 2);
        Assert.Equal(1.0, s[1].VideoSec);
    }

    [Fact]
    public void Parses_newer_bracketed_cues()
    {
        var s = SrtLog.Parse("""
            1
            00:00:00,033 --> 00:00:00,066
            <font size="28">FrameCnt: 1, DiffTime: 33ms
            [iso: 400] [shutter: 1/320.0] [fnum: 1.7] [ev: 2.0] [latitude: 37.361234] [longitude: -113.618765] [rel_alt: 61.500 abs_alt: 1650.309] [ct: 5695] </font>
            """);
        var only = Assert.Single(s);
        Assert.Equal(37.361234, only.Lat, 6);
        Assert.Equal(-113.618765, only.Lon, 6);
        Assert.Equal(61.5, only.HeightM, 3);
        Assert.Equal(0.033, only.VideoSec, 3);
    }

    [Fact]
    public void Interpolates_between_seconds()
    {
        var s = SrtLog.Parse(MavicMini);
        var mid = SrtLog.At(s, 0.5);
        Assert.Equal(48.0772, mid.Lat, 4);
        Assert.Equal(201.70, mid.HeightM, 2);
    }
}

public class ProjectionTests
{
    [Fact]
    public void Field_of_view_from_a_ground_target_inverts_the_pinhole_model()
    {
        // At 30 m, a 71.5° camera covers 2·30·tan(35.75°) = 43.2 m across 1920 px; a 20 m tape spans 889 px.
        var groundWidth = 2 * 30 * Math.Tan(71.5 * Math.PI / 360);
        var tapePx = 1920 * 20 / groundWidth;
        Assert.Equal(71.5, CameraModel.HfovFromGroundTarget(1920, 20, tapePx, 30), 6);
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraModel.HfovFromGroundTarget(1920, 20, 0, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => CameraModel.HfovFromGroundTarget(1920, 20, 2000, 30));
    }

    private static readonly CameraModel Cam = CameraModel.WithAspect(800, 600, 71.5);

    [Fact]
    public void Nadir_centre_pixel_is_straight_below()
    {
        var p = new GroundProjector(Cam, null).PixelToGround(400, 300, new CameraPose(new Coordinate(10, 20), 100, 37, -90))!;
        Assert.Equal(10, p.X, 6);
        Assert.Equal(20, p.Y, 6);
    }

    [Fact]
    public void Nadir_image_edge_is_altitude_times_tan_half_fov()
    {
        // Heading 0 (north up): the right edge of the image is east.
        var p = new GroundProjector(Cam, null).PixelToGround(800, 300, new CameraPose(new Coordinate(0, 0), 100, 0, -90))!;
        Assert.Equal(100 * Math.Tan(71.5 * Math.PI / 360), p.X, 3);
        Assert.Equal(0, p.Y, 3);
    }

    [Fact]
    public void Forty_five_degree_pitch_looks_altitude_ahead_along_heading()
    {
        var p = new GroundProjector(Cam, null).PixelToGround(400, 300, new CameraPose(new Coordinate(0, 0), 100, 90, -45))!;
        Assert.Equal(100, p.X, 6);   // heading 90: east
        Assert.Equal(0, p.Y, 6);
    }

    [Fact]
    public void Rays_near_the_horizon_are_rejected()
    {
        Assert.Null(new GroundProjector(Cam, null).PixelToGround(400, 300, new CameraPose(new Coordinate(0, 0), 100, 0, -3)));
    }

    [Fact]
    public void Terrain_intersection_lands_on_the_slope()
    {
        // Ground rising 10 m per 30 m cell to the east; camera 300 m above the origin's ground, looking east and down.
        var flat = TerrainGrid.Flat(new Envelope(-1500, 1500, -1500, 1500), 30);
        var z = new double[flat.Nx * flat.Ny];
        for (var j = 0; j < flat.Ny; j++) for (var i = 0; i < flat.Nx; i++) z[j * flat.Nx + i] = 1000 + (i - flat.Nx / 2.0) * 10;
        var terrain = new TerrainGrid(flat.Nx, flat.Ny, flat.X0, flat.Y0, 30, z);
        var pose = new CameraPose(new Coordinate(0, 0), terrain.Elevation(0, 0) + 300, 90, -45);

        var hit = new GroundProjector(Cam, terrain).PixelToGround(400, 300, pose)!;
        // On a 45-degree ray, horizontal distance = drop in height. Ground rises towards the camera's view, so the hit is short of 300 m.
        var drop = pose.AltitudeM - terrain.Elevation(hit.X, hit.Y);
        Assert.InRange(hit.X - drop, -1.0, 1.0);
        Assert.True(hit.X < 300);
    }
}

public class DetectionTests
{
    [Theory]
    [InlineData(235, 140, 25, true)]    // flame orange
    [InlineData(250, 220, 120, true)]   // bright yellow flame
    [InlineData(168, 154, 112, false)]  // dry tan grass: passes Chen's RGB rule, fails the chrominance rule
    [InlineData(86, 104, 70, false)]    // juniper green
    [InlineData(40, 38, 35, false)]     // burn scar
    [InlineData(150, 185, 220, false)]  // sky
    public void Flame_rule(byte r, byte g, byte b, bool expected) =>
        Assert.Equal(expected, ColorFireDetector.IsFlame(r, g, b));

    [Fact]
    public void Detects_a_flame_patch_and_ignores_a_lone_shadow()
    {
        var img = new RgbImage(100, 100);
        for (var y = 0; y < 100; y++) for (var x = 0; x < 100; x++) img[x, y] = (168, 154, 112);
        for (var y = 10; y < 30; y++) for (var x = 10; x < 30; x++) img[x, y] = (235, 130, 25);   // flame, 400 px
        for (var y = 60; y < 80; y++) for (var x = 60; x < 80; x++) img[x, y] = (40, 38, 35);     // shadow with no flame
        var mask = new ColorFireDetector().Detect(img);
        Assert.Equal(400, mask.Count);
        Assert.True(mask[15, 15]);
        Assert.False(mask[70, 70]);
    }

    [Fact]
    public void Png_round_trips()
    {
        var img = new RgbImage(7, 5);
        for (var y = 0; y < 5; y++) for (var x = 0; x < 7; x++) img[x, y] = ((byte)(x * 30), (byte)(y * 40), (byte)(x + y));
        var back = Png.Decode(Png.Encode(img));
        Assert.Equal(img.Pixels, back.Pixels);
    }
}

/// <summary>
/// End to end without real footage: render the scenario's true fire through a virtual survey camera,
/// detect it, project it back, and compare with the truth.
/// </summary>
public class SurveyPipelineTests
{
    [Fact]
    public void Detected_front_matches_the_true_fire()
    {
        var scenario = ScenarioLoader.LoadFile(Path.Combine(RepoRoot(), "scenarios", "pine-valley-wind-shift.yaml"));
        var terrain = scenario.Terrain!;
        var truth = scenario.Fire.FrontAt(30);
        var centre = truth.Centroid.Coordinate;

        // A survey camera 1500 m above the fire (an incident-awareness aircraft, not a Mavic Mini at 120 m).
        var cam = CameraModel.WithAspect(800, 600, 71.5);
        var projector = new GroundProjector(cam, terrain);
        var pose = new CameraPose(centre, terrain.Elevation(centre.X, centre.Y) + 1500, 0, -90);
        var frame = new SyntheticCamera(projector).Render(pose, truth);
        var mask = new ColorFireDetector().Detect(frame);

        var mapper = new FrontMapper(terrain, projector);
        mapper.Add(mask, pose);
        var detected = mapper.Front();

        var iou = detected.Intersection(truth).Area / detected.Union(truth).Area;
        Assert.True(iou > 0.85, $"IoU {iou:0.00}");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lookout.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}

/// <summary>The full offline path on a generated video: ffmpeg frames, .SRT poses, detection, mapping.</summary>
public class VideoSurveyTests
{
    [Fact]
    public void Survey_of_a_synthetic_flight_recovers_the_fire()
    {
        if (!FfmpegAvailable()) Assert.Skip("ffmpeg is not installed");
        var scenario = ScenarioLoader.LoadFile(Path.Combine(RepoRoot(), "scenarios", "pine-valley-wind-shift.yaml"));
        var terrain = scenario.Terrain!;
        var truth = scenario.Fire.FrontAt(30);
        var c = truth.Centroid.Coordinate;
        var camera = CameraModel.WithAspect(640, 480, 71.5);
        var dir = Directory.CreateTempSubdirectory("lookout-flight-").FullName;

        // A 1400 m high pass across the fire, south to north, camera straight down.
        var (video, srt) = SyntheticFlight.Write(dir, scenario.Frame, terrain, camera, truth,
            new Coordinate(c.X, c.Y - 300), new Coordinate(c.X, c.Y + 300), 1400, headingDeg: 0, pitchDeg: -90, seconds: 6);

        var survey = new VideoSurvey(scenario.Frame, terrain, camera, new ColorFireDetector());
        var result = survey.Run(video, SrtLog.ParseFile(srt), attitude: null, new SurveyOptions(Fps: 1, HeadingDeg: 0, GimbalPitchDeg: -90));

        Assert.Equal(6, result.Frames.Count);
        var iou = result.Front.Intersection(truth).Area / result.Front.Union(truth).Area;
        Assert.True(iou > 0.75, $"IoU {iou:0.00} (video compression and 4-decimal GPS included)");
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, UseShellExecute = false });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lookout.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}

/// <summary>
/// The ONNX path, without real weights: a tiny model with the RF-DETR export interface that always
/// returns one confident box (top-left) and one low-score box (bottom-right). Built by
/// tools/make_test_detector.py.
/// </summary>
public class OnnxDetectorTests
{
    [Fact]
    public void Reads_the_graph_thresholds_scores_and_maps_boxes_to_pixels()
    {
        var model = Path.Combine(AppContext.BaseDirectory, "fixtures", "onnx", "tiny-detector.onnx");
        using var detector = new OnnxFireDetector(model, threshold: 0.4f);
        Assert.Contains("64x64", detector.Name); // input size read from the model

        var frame = new RgbImage(100, 100);
        var mask = detector.Detect(frame);

        // Box 1: centre (25, 25), 20 x 20 px, score 0.95 -> pixels 15..35 inclusive.
        Assert.True(mask[25, 25]);
        Assert.True(mask[15, 15]);
        Assert.True(mask[35, 35]);
        Assert.False(mask[14, 25]);
        Assert.Equal(21 * 21, mask.Count);
        // Box 2: score 0.05, under the threshold.
        Assert.False(mask[75, 75]);
    }

    [Fact]
    public void Class_filter_can_exclude_every_detection()
    {
        var model = Path.Combine(AppContext.BaseDirectory, "fixtures", "onnx", "tiny-detector.onnx");
        using var detector = new OnnxFireDetector(model, threshold: 0.4f, fireClasses: [1]);
        Assert.Equal(0, detector.Detect(new RgbImage(100, 100)).Count);
    }
}
