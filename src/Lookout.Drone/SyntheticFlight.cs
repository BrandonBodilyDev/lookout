using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lookout.Engine.Geo;
using Lookout.Engine.Terrain;
using NetTopologySuite.Geometries;

namespace Lookout.Drone;

/// <summary>
/// Makes a test flight: a video of a scenario's fire seen from a camera flying a straight line,
/// plus the matching DJI subtitle track in the original Mavic Mini layout (one cue per second,
/// longitude first, four decimals). Rounding to four decimals reproduces the real track's roughly
/// 11 m position error, so the survey pipeline is tested with the error it will meet in the field.
/// </summary>
public static class SyntheticFlight
{
    public static (string Video, string Srt) Write(
        string outDir, LocalFrame frame, TerrainGrid terrain, CameraModel camera, Geometry burned,
        Coordinate from, Coordinate to, double heightAboveTakeoffM, double headingDeg, double pitchDeg, int seconds, int fps = 2)
    {
        Directory.CreateDirectory(outDir);
        var framesDir = Path.Combine(outDir, "frames");
        Directory.CreateDirectory(framesDir);
        var projector = new GroundProjector(camera, terrain);
        var renderer = new SyntheticCamera(projector);
        var takeoffGround = terrain.Elevation(from.X, from.Y);
        var srt = new StringBuilder();

        Coordinate At(double t)
        {
            var f = seconds == 0 ? 0 : t / seconds;
            return new Coordinate(from.X + f * (to.X - from.X), from.Y + f * (to.Y - from.Y));
        }

        for (var i = 0; i < seconds * fps; i++)
        {
            var pos = At((double)i / fps);
            var pose = new CameraPose(pos, takeoffGround + heightAboveTakeoffM, headingDeg, pitchDeg);
            renderer.Render(pose, burned, seed: i + 1).WritePng(Path.Combine(framesDir, $"f_{i:00000}.png"));
        }

        for (var s = 0; s <= seconds; s++)
        {
            var (lon, lat) = frame.ToLonLat(At(s));
            srt.Append(CultureInfo.InvariantCulture,
                $"{s + 1}\n{Tc(s)} --> {Tc(s + 1)}\nF/2.8, SS 500.00, ISO 100, EV 0, GPS ({lon:0.0000}, {lat:0.0000}, 14), D {from.Distance(At(s)):0.00}m, H {heightAboveTakeoffM:0.00}m, H.S n/a, V.S n/a\n\n");
        }

        var video = Path.Combine(outDir, "synthetic.mp4");
        var srtPath = Path.Combine(outDir, "synthetic.SRT");
        File.WriteAllText(srtPath, srt.ToString());
        using var p = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-v error -y -framerate {fps} -i \"{Path.Combine(framesDir, "f_%05d.png")}\" -c:v libx264 -crf 12 -pix_fmt yuv420p \"{video}\"")
            { RedirectStandardError = true, UseShellExecute = false }) ?? throw new InvalidOperationException("ffmpeg not found.");
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed: {err}");
        return (video, srtPath);
    }

    private static string Tc(int s) => $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00},000";
}
