using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Lookout.Api.Tak;
using Lookout.Api.Weather;

namespace Lookout.Api.Tests;

public class CotTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 20, 20, 0, TimeSpan.Zero);

    [Fact]
    public void Crew_event_has_the_fields_TAK_clients_need()
    {
        var incident = Guid.NewGuid();
        var e = CotBuilder.Crew(incident, "ALPHA", 37.3591, -113.62, "critical", "LEAVE NOW via ER-1 to SZ-1", 87, Now);

        Assert.Equal("a-f-G-U-C", (string?)e.Attribute("type"));
        Assert.Equal($"lookout-{incident:N}-crew-ALPHA", (string?)e.Attribute("uid"));
        Assert.Equal("2026-07-14T20:22:00.000Z", (string?)e.Attribute("stale"));
        Assert.Equal("ALPHA", (string?)e.Element("detail")!.Element("contact")!.Attribute("callsign"));
        Assert.Equal("37.3591000", (string?)e.Element("point")!.Attribute("lat"));
        Assert.Contains("CRITICAL", (string?)e.Element("detail")!.Element("remarks"));
    }

    [Fact]
    public void A_cot_position_is_too_big_for_the_crew_downlink()
    {
        // The reason CoT is produced only on the IC side: compare with the 16-byte binary fix.
        var e = CotBuilder.Crew(Guid.NewGuid(), "ALPHA", 37.3591, -113.62, "none", null, null, Now);
        Assert.True(Encoding.UTF8.GetByteCount(CotBuilder.Serialize(e)) > Link.WireFormat.MaxDownlinkBytes);
    }

    [Fact]
    public void Fire_perimeter_is_a_freeform_shape_with_one_link_per_vertex()
    {
        double[][] ring = [[-113.63, 37.35], [-113.62, 37.35], [-113.62, 37.36], [-113.63, 37.35]];
        var e = CotBuilder.FirePerimeter(Guid.NewGuid(), ring, Now);
        Assert.Equal("u-d-f", (string?)e.Attribute("type"));
        Assert.Equal(4, e.Element("detail")!.Elements("link").Count());
        Assert.Equal("37.3500000,-113.6300000", (string?)e.Element("detail")!.Elements("link").First().Attribute("point"));
        Assert.NotNull(XElement.Parse(CotBuilder.Serialize(e)));
    }
}

public class NwsTests
{
    private static JsonDocument Fixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "nws", name)));

    [Fact]
    public void Parses_recorded_observation_and_hourly_forecast()
    {
        var c = NwsClient.Parse("SLC/20,31", "KSGU", Fixture("observation.json"), Fixture("hourly.json"));
        Assert.Equal("KSGU", c.Station);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 5, 25, 0, TimeSpan.Zero), c.ObservedAt);
        Assert.Equal(0, c.WindKmh);
        Assert.Equal(12, c.Forecast.Count);
        Assert.All(c.Forecast, h => Assert.True(h.WindKmh >= 0));
        Assert.Contains(c.Forecast, h => h.WindFromDeg is not null);
    }

    [Fact]
    public void Midflame_wind_uses_reference_height_and_adjustment_factor()
    {
        // 36 km/h at 10 m = 10 m/s; x 0.87 to 20 ft; x 0.4 sheltering = 3.48 m/s.
        Assert.Equal(3.48, NwsClient.MidflameMs(36), 2);
    }
}
