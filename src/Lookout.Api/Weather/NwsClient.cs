using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Lookout.Api.Weather;

/// <summary>
/// National Weather Service API (api.weather.gov): the nearest observation station's latest wind,
/// and the hourly forecast for the grid cell. NWS asks every client to send a User-Agent with
/// contact details, and serves cached data, so results here are cached for ten minutes.
/// </summary>
public sealed class NwsClient(HttpClient http, TimeProvider clock)
{
    public const string UserAgent = "lookout (+https://github.com/BrandonBodilyDev/lookout)";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, NwsConditions Value)> _cache = new();

    public async Task<NwsConditions> GetAsync(double lat, double lon, CancellationToken ct = default)
    {
        var key = FormattableString.Invariant($"{lat:0.0000},{lon:0.0000}");
        if (_cache.TryGetValue(key, out var hit) && clock.GetUtcNow() - hit.At < CacheFor) return hit.Value;

        using var points = await GetJson($"https://api.weather.gov/points/{key}", ct);
        var props = points.RootElement.GetProperty("properties");
        var grid = $"{props.GetProperty("gridId").GetString()}/{props.GetProperty("gridX").GetInt32()},{props.GetProperty("gridY").GetInt32()}";

        using var stations = await GetJson(props.GetProperty("observationStations").GetString()! + "?limit=1", ct);
        var station = stations.RootElement.GetProperty("features")[0].GetProperty("properties").GetProperty("stationIdentifier").GetString()!;

        using var obs = await GetJson($"https://api.weather.gov/stations/{station}/observations/latest", ct);
        using var hourly = await GetJson(props.GetProperty("forecastHourly").GetString()!, ct);

        var result = Parse(grid, station, obs, hourly);
        _cache[key] = (clock.GetUtcNow(), result);
        return result;
    }

    /// <summary>Pure parsing, separate from HTTP so tests can feed recorded responses.</summary>
    public static NwsConditions Parse(string grid, string station, JsonDocument observation, JsonDocument hourly)
    {
        var o = observation.RootElement.GetProperty("properties");
        var periods = hourly.RootElement.GetProperty("properties").GetProperty("periods").EnumerateArray().Take(12)
            .Select(p => new NwsHour(
                p.GetProperty("startTime").GetDateTimeOffset(),
                ParseMph(p.GetProperty("windSpeed").GetString()) * 1.609344,
                CompassToDegrees(p.GetProperty("windDirection").GetString()),
                p.GetProperty("temperature").GetDouble(),
                p.TryGetProperty("relativeHumidity", out var rh) ? Value(rh) : null,
                p.GetProperty("shortForecast").GetString() ?? ""))
            .ToList();

        return new NwsConditions(
            grid, station,
            o.GetProperty("timestamp").GetDateTimeOffset(),
            Value(o.GetProperty("windDirection")),
            Value(o.GetProperty("windSpeed")),
            o.TryGetProperty("windGust", out var g) ? Value(g) : null,
            o.TryGetProperty("relativeHumidity", out var orh) ? Value(orh) : null,
            periods);
    }

    /// <summary>
    /// NWS wind is a 10 m wind. The spread model wants midflame wind: about 0.87 of the 10 m wind
    /// at the 20 ft reference height, times a wind adjustment factor (0.4 default: partially
    /// sheltered surface fuels). See docs/assumptions.md.
    /// </summary>
    public static double MidflameMs(double windKmh, double adjustmentFactor = 0.4) => windKmh / 3.6 * 0.87 * adjustmentFactor;

    private async Task<JsonDocument> GetJson(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.ParseAdd("application/geo+json");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static double? Value(JsonElement e) =>
        e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>"10 mph" or "10 to 15 mph" (the upper bound is used, to be conservative).</summary>
    private static double ParseMph(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var numbers = s.Split(' ').Where(w => double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .Select(w => double.Parse(w, CultureInfo.InvariantCulture)).ToList();
        return numbers.Count == 0 ? 0 : numbers.Max();
    }

    private static readonly string[] Points16 = ["N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW"];

    private static double? CompassToDegrees(string? s)
    {
        var i = Array.IndexOf(Points16, s);
        return i < 0 ? null : i * 22.5;
    }
}

/// <param name="WindFromDeg">Direction the wind blows from, degrees true; null when calm or missing.</param>
/// <param name="WindKmh">10 m wind speed, km/h.</param>
public sealed record NwsConditions(
    string Grid, string Station, DateTimeOffset ObservedAt, double? WindFromDeg, double? WindKmh, double? GustKmh,
    double? RelativeHumidity, IReadOnlyList<NwsHour> Forecast);

public sealed record NwsHour(DateTimeOffset Start, double WindKmh, double? WindFromDeg, double TemperatureF, double? RelativeHumidity, string Summary);
