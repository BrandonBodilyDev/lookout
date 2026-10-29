using System.Text.Json;

namespace Lookout.Link;

/// <summary>
/// Messages on the drone / ground-station link. That link is broadband (in Baxter-style setups, a
/// Starlink or LTE backhaul), so these are plain JSON rather than packed bytes.
/// </summary>
public static class SurveyJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static byte[] Encode<T>(T message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static T Decode<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, Options) ?? throw new FormatException($"Empty {typeof(T).Name}.");
}

/// <summary>The burned area as last surveyed: GeoJSON MultiPolygon coordinates in [lon, lat].</summary>
public sealed record DroneFrontMessage(DateTimeOffset Time, double[][][][] Coordinates, string Source);

/// <summary>Wind observed at the fire, plus the head-fire rate the spread model should use.</summary>
public sealed record WeatherMessage(DateTimeOffset Time, double WindFromDeg, double MidflameWindMs, double HeadRateMPerMin, string Source);
