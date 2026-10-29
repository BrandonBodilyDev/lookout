using System.Globalization;

namespace Lookout.Drone;

/// <summary>Heading and gimbal pitch at a moment of the video.</summary>
public sealed record Attitude(double VideoSec, double HeadingDeg, double GimbalPitchDeg);

/// <summary>
/// Reads heading and gimbal pitch from an Airdata-style flight-log CSV (the DJI .SRT has neither).
/// Columns are found by name, case-insensitively: a time column in milliseconds ("time(millisecond)"),
/// "compass_heading", "gimbal_pitch", and "isVideo". Only rows recorded while video was rolling are
/// used, and video time starts at the first of them. Column names follow Airdata's export as described
/// in public write-ups; they are not verified against a real export in this repo.
/// </summary>
public static class FlightLog
{
    public static IReadOnlyList<Attitude> ParseCsv(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return [];
        var header = lines[0].Split(',').Select(h => h.Trim().ToLowerInvariant()).ToArray();
        int Col(string prefix) => Array.FindIndex(header, h => h.StartsWith(prefix, StringComparison.Ordinal));
        int time = Col("time(millisecond)"), heading = Col("compass_heading"), pitch = Col("gimbal_pitch"), video = Col("isvideo");
        if (time < 0 || heading < 0 || pitch < 0)
            throw new FormatException("Flight log needs time(millisecond), compass_heading and gimbal_pitch columns.");

        var rows = new List<Attitude>();
        double? firstVideoMs = null;
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(',');
            if (f.Length <= Math.Max(time, Math.Max(heading, pitch))) continue;
            if (video >= 0 && f.Length > video && f[video].Trim() != "1") continue;
            var ms = double.Parse(f[time], CultureInfo.InvariantCulture);
            firstVideoMs ??= ms;
            rows.Add(new Attitude((ms - firstVideoMs.Value) / 1000.0,
                double.Parse(f[heading], CultureInfo.InvariantCulture),
                double.Parse(f[pitch], CultureInfo.InvariantCulture)));
        }
        return rows;
    }

    /// <summary>Attitude nearest in time (headings are not interpolated across north).</summary>
    public static Attitude At(IReadOnlyList<Attitude> rows, double videoSec) =>
        rows.MinBy(r => Math.Abs(r.VideoSec - videoSec)) ?? throw new ArgumentException("Empty flight log.", nameof(rows));
}
