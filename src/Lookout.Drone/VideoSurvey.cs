using System.Diagnostics;
using Lookout.Engine.Geo;
using Lookout.Engine.Terrain;
using NetTopologySuite.Geometries;

namespace Lookout.Drone;

/// <param name="Fps">Frames analysed per second of video. Fire moves slowly; 1 is plenty.</param>
/// <param name="HeadingDeg">Heading to use when there is no flight log.</param>
/// <param name="GimbalPitchDeg">Gimbal pitch to use when there is no flight log (-90 is straight down).</param>
public sealed record SurveyOptions(double Fps = 1, double HeadingDeg = 0, double GimbalPitchDeg = -90, string? FramesDir = null);

public sealed record SurveyFrame(double VideoSec, double Lat, double Lon, double HeightM, int FirePixels);

public sealed record SurveyResult(Geometry Front, IReadOnlyList<SurveyFrame> Frames, int BurnedCells);

/// <summary>
/// Offline survey of a drone video: extract frames with ffmpeg, give each frame a camera pose from
/// the subtitle track (and the flight log when present), detect fire, project it onto the terrain
/// grid, and accumulate a burned-area polygon in the scenario's frame.
///
/// Height comes from the .SRT "H" (above take-off). The take-off point's ground elevation is read
/// from the terrain grid under the first sample, so camera altitude = take-off ground + H.
/// </summary>
public sealed class VideoSurvey(LocalFrame frame, TerrainGrid terrain, CameraModel camera, IFireDetector detector)
{
    public SurveyResult Run(string videoPath, IReadOnlyList<DroneSample> track, IReadOnlyList<Attitude>? attitude, SurveyOptions options)
    {
        if (track.Count == 0) throw new ArgumentException("The subtitle track has no GPS samples.", nameof(track));
        var dir = options.FramesDir ?? Directory.CreateTempSubdirectory("lookout-frames-").FullName;
        Directory.CreateDirectory(dir);
        ExtractFrames(videoPath, dir, options.Fps, camera.Width, camera.Height);

        var projector = new GroundProjector(camera, terrain);
        var mapper = new FrontMapper(terrain, projector);
        var home = frame.ToLocal(track[0].Lon, track[0].Lat);
        var takeoffGround = terrain.Elevation(home.X, home.Y);
        var frames = new List<SurveyFrame>();

        foreach (var (file, index) in Directory.GetFiles(dir, "frame_*.png").Order().Select((f, i) => (f, i)))
        {
            var t = index / options.Fps;
            var s = SrtLog.At(track, t);
            var a = attitude is { Count: > 0 } ? FlightLog.At(attitude, t) : new Attitude(t, options.HeadingDeg, options.GimbalPitchDeg);
            var pose = new CameraPose(frame.ToLocal(s.Lon, s.Lat), takeoffGround + s.HeightM, a.HeadingDeg, a.GimbalPitchDeg);
            var mask = detector.Detect(RgbImage.ReadPng(file));
            mapper.Add(mask, pose);
            frames.Add(new SurveyFrame(t, s.Lat, s.Lon, s.HeightM, mask.Count));
        }
        return new SurveyResult(mapper.Front(), frames, mapper.BurnedCells);
    }

    /// <summary>ffmpeg writes RGB PNG frames scaled to the camera model's size.</summary>
    public static void ExtractFrames(string videoPath, string dir, double fps, int width, int height) =>
        RunFfmpeg($"-v error -i \"{videoPath}\" -vf fps={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)},scale={width}:{height} -pix_fmt rgb24 \"{Path.Combine(dir, "frame_%05d.png")}\"");

    /// <summary>The Mavic Mini embeds its subtitle track in the MP4; ffmpeg can write it out as .SRT.</summary>
    public static string ExtractSubtitles(string videoPath, string srtPath)
    {
        RunFfmpeg($"-v error -y -i \"{videoPath}\" -map 0:s:0 \"{srtPath}\"");
        return srtPath;
    }

    private static void RunFfmpeg(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("ffmpeg", args) { RedirectStandardError = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not start ffmpeg. Is it installed and on PATH?");
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed: {err.Trim()}");
    }
}
