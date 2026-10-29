using System.Globalization;
using System.Text.RegularExpressions;

namespace Lookout.Drone;

/// <summary>One telemetry sample from a DJI video subtitle (.SRT) track.</summary>
/// <param name="VideoSec">Seconds from the start of the video.</param>
/// <param name="HeightM">Height above the take-off point, metres (barometric).</param>
/// <param name="DistanceFromHomeM">Horizontal distance from the home point, if the format has it.</param>
public sealed record DroneSample(double VideoSec, double Lat, double Lon, double HeightM, double? DistanceFromHomeM);

/// <summary>
/// Parses DJI video subtitle logs. Two layouts are accepted:
///
/// Original Mavic Mini, one cue per second, longitude first, four decimals (about 11 m):
///   F/2.8, SS 30.15, ISO 260, EV -1.7, GPS (-121.7458, 48.0771, 17), D 221.21m, H 200.70m, H.S n/a, V.S n/a
///
/// Newer DJI drones (Mini 3/4, Air 3, Mavic 3), one cue per frame:
///   [iso: 400] [shutter: 1/320.0] ... [latitude: 30.123456] [longitude: -81.123456] [rel_alt: 6.500 abs_alt: -32.309]
///
/// Neither layout carries heading or gimbal pitch; those come from the flight log or are supplied.
/// On the Mavic Mini the third GPS value appears to be a satellite count, not altitude, so H is used.
/// </summary>
public static partial class SrtLog
{
    [GeneratedRegex(@"^(\d\d):(\d\d):(\d\d),(\d{3})\s*-->")]
    private static partial Regex Timecode();

    [GeneratedRegex(@"GPS\s*\(\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\).*?\bD\s+(-?\d+(?:\.\d+)?)m.*?\bH\s+(-?\d+(?:\.\d+)?)m")]
    private static partial Regex MiniLine();

    [GeneratedRegex(@"\[latitude:\s*(-?\d+(?:\.\d+)?)\]\s*\[longitude:\s*(-?\d+(?:\.\d+)?)\].*?\[rel_alt:\s*(-?\d+(?:\.\d+)?)")]
    private static partial Regex BracketLine();

    public static IReadOnlyList<DroneSample> Parse(string text)
    {
        var samples = new List<DroneSample>();
        double? cueStart = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var tc = Timecode().Match(line);
            if (tc.Success)
            {
                cueStart = int.Parse(tc.Groups[1].Value) * 3600 + int.Parse(tc.Groups[2].Value) * 60
                    + int.Parse(tc.Groups[3].Value) + int.Parse(tc.Groups[4].Value) / 1000.0;
                continue;
            }
            if (cueStart is null) continue;

            var mini = MiniLine().Match(line);
            if (mini.Success)
            {
                samples.Add(new DroneSample(cueStart.Value, D(mini.Groups[2]), D(mini.Groups[1]), D(mini.Groups[5]), D(mini.Groups[4])));
                cueStart = null;
                continue;
            }
            var bracket = BracketLine().Match(line);
            if (bracket.Success)
            {
                samples.Add(new DroneSample(cueStart.Value, D(bracket.Groups[1]), D(bracket.Groups[2]), D(bracket.Groups[3]), null));
                cueStart = null;
            }
        }
        // Some firmware writes 0,0 before the GPS fix; those are not positions.
        return samples.Where(s => s.Lat != 0 || s.Lon != 0).OrderBy(s => s.VideoSec).ToList();
    }

    public static IReadOnlyList<DroneSample> ParseFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>Position at <paramref name="videoSec"/>, linearly interpolated between samples (clamped at the ends).</summary>
    public static DroneSample At(IReadOnlyList<DroneSample> samples, double videoSec)
    {
        if (samples.Count == 0) throw new ArgumentException("No samples.", nameof(samples));
        if (videoSec <= samples[0].VideoSec) return samples[0] with { VideoSec = videoSec };
        for (var i = 1; i < samples.Count; i++)
        {
            if (videoSec > samples[i].VideoSec) continue;
            var a = samples[i - 1];
            var b = samples[i];
            var f = (videoSec - a.VideoSec) / (b.VideoSec - a.VideoSec);
            return new DroneSample(videoSec, a.Lat + f * (b.Lat - a.Lat), a.Lon + f * (b.Lon - a.Lon), a.HeightM + f * (b.HeightM - a.HeightM), null);
        }
        return samples[^1] with { VideoSec = videoSec };
    }

    private static double D(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);
}
