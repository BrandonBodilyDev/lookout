using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Lookout.Api.Tak;

/// <summary>
/// Cursor on Target (CoT) events for TAK clients (ATAK, WinTAK, iTAK), the common operating picture
/// used across wildland fire. Built on the incident-commander side only: a minimal CoT position is
/// around 400 bytes of XML, too large for the 270-byte crew downlink, so the crew link stays binary
/// and this export translates at the server, much as Meshtastic gateways do for LoRa.
///
/// Event types follow the MIL-STD-2525 based CoT type tree: a-f-G-U-C (friendly ground unit, combat)
/// for crews, u-d-f (user-drawn freeform shape) for the fire perimeter, and b-m-p-s-p-i (spot map
/// point of interest) for cut-off alerts. This is an unofficial export, not a certified TAK integration.
/// </summary>
public static class CotBuilder
{
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    private static string F(double v, string fmt = "0.0000000") => v.ToString(fmt, CultureInfo.InvariantCulture);

    public static XElement Event(string uid, string type, double lat, double lon, DateTimeOffset now, TimeSpan stale, XElement detail, string how = "m-g") =>
        new("event",
            new XAttribute("version", "2.0"),
            new XAttribute("uid", uid),
            new XAttribute("type", type),
            new XAttribute("how", how),
            new XAttribute("time", Iso(now)),
            new XAttribute("start", Iso(now)),
            new XAttribute("stale", Iso(now + stale)),
            new XElement("point",
                new XAttribute("lat", F(lat)), new XAttribute("lon", F(lon)),
                new XAttribute("hae", "9999999.0"), new XAttribute("ce", "10.0"), new XAttribute("le", "9999999.0")),
            detail);

    /// <summary>A crew's last known position, with its Lookout level in the remarks. Stale after two minutes, like AFF.</summary>
    public static XElement Crew(Guid incident, string crewId, double lat, double lon, string level, string? summary, int? battery, DateTimeOffset now) =>
        Event($"lookout-{incident:N}-crew-{crewId}", "a-f-G-U-C", lat, lon, now, TimeSpan.FromMinutes(2),
            new XElement("detail",
                new XElement("contact", new XAttribute("callsign", crewId)),
                battery is { } b ? new XElement("status", new XAttribute("battery", b)) : null,
                new XElement("remarks", $"Lookout {level.ToUpperInvariant()}" + (summary is null ? "" : $": {summary}"))));

    /// <summary>The surveyed fire perimeter as a filled freeform shape.</summary>
    public static XElement FirePerimeter(Guid incident, IReadOnlyList<double[]> ringLonLat, DateTimeOffset now)
    {
        var lat = ringLonLat.Average(p => p[1]);
        var lon = ringLonLat.Average(p => p[0]);
        return Event($"lookout-{incident:N}-fire", "u-d-f", lat, lon, now, TimeSpan.FromMinutes(5),
            new XElement("detail",
                ringLonLat.Select(p => new XElement("link", new XAttribute("point", $"{F(p[1])},{F(p[0])}"))),
                new XElement("strokeColor", new XAttribute("value", unchecked((int)0xFFB42318))),
                new XElement("fillColor", new XAttribute("value", unchecked((int)0x66E8542F))),
                new XElement("strokeWeight", new XAttribute("value", "3.0")),
                new XElement("contact", new XAttribute("callsign", "Fire perimeter (Lookout)")),
                new XElement("remarks", "Burned area from the latest drone survey")),
            how: "h-e");
    }

    /// <summary>
    /// A crew's current cut-off warning, placed where the fire would cut its escape route. One uid per
    /// crew, so TAK clients update the marker in place instead of collecting stale ones.
    /// </summary>
    public static XElement CutoffWarning(Guid incident, string crewId, double lat, double lon, string level, string text, DateTimeOffset now) =>
        Event($"lookout-{incident:N}-cutoff-{crewId}", "b-m-p-s-p-i", lat, lon, now, TimeSpan.FromMinutes(5),
            new XElement("detail",
                new XElement("contact", new XAttribute("callsign", $"{crewId} {level.ToUpperInvariant()}")),
                new XElement("color", new XAttribute("argb", level == "critical" ? unchecked((int)0xFFDC2626) : unchecked((int)0xFFEE7417))),
                new XElement("remarks", text)),
            how: "h-e");

    public static string Serialize(XElement e) => e.ToString(SaveOptions.DisableFormatting);
}

/// <summary>Streams CoT XML to a TAK server's plain TCP input (FreeTAKServer and TAK Server both offer one, often port 8087).</summary>
public sealed class CotSender(CotSettings settings, ILogger<CotSender> log) : IAsyncDisposable
{
    private TcpClient? _client;
    private DateTimeOffset _nextAttempt;

    public bool Enabled => settings.Host is { Length: > 0 };

    public async Task SendAsync(IEnumerable<XElement> events, CancellationToken ct)
    {
        if (!Enabled || DateTimeOffset.UtcNow < _nextAttempt) return;
        try
        {
            if (_client is not { Connected: true })
            {
                _client?.Dispose();
                _client = new TcpClient();
                await _client.ConnectAsync(settings.Host!, settings.Port, ct);
                log.LogInformation("CoT export connected to {Host}:{Port}", settings.Host, settings.Port);
            }
            var stream = _client.GetStream();
            foreach (var e in events)
                await stream.WriteAsync(Encoding.UTF8.GetBytes(CotBuilder.Serialize(e)), ct);
            await stream.FlushAsync(ct);
        }
        catch (Exception e) when (e is SocketException or IOException)
        {
            log.LogWarning("CoT export to {Host}:{Port} failed ({Error}); retrying in 30 s", settings.Host, settings.Port, e.Message);
            _client?.Dispose();
            _client = null;
            _nextAttempt = DateTimeOffset.UtcNow.AddSeconds(30);
        }
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class CotSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 8087;
    /// <summary>Real seconds between exports.</summary>
    public double IntervalSec { get; set; } = 10;
}
