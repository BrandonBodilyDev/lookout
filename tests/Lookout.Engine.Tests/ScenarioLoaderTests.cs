using Lookout.Engine.Scenarios;

namespace Lookout.Engine.Tests;

public class ScenarioLoaderTests
{
    private const string Minimal = """
        id: t
        name: Test
        description: test
        start: "2026-07-14T14:00:00-06:00"
        duration_min: 10
        origin: { lon: -113.62, lat: 37.36 }
        fire: { ignition: [-113.63, 37.36], ignition_min: 0 }
        wind:
          - { start_min: 0, from_deg: 270, midflame_wind_ms: 4, head_rate_m_per_min: 30 }
        safety_zones:
          - id: SZ
            name: zone
            polygon: [[-113.6105, 37.3595], [-113.6095, 37.3595], [-113.6095, 37.3605], [-113.6105, 37.3605]]
        routes:
          - id: R
            name: route
            safety_zone: SZ
            vertices: [[-113.62, 37.36, 1500], [-113.61, 37.36, 1520]]
        crews:
          - id: A
            name: Alpha
            route: R
            track: [[0, -113.62, 37.36]]
            comms_gaps: [[2, 6]]
        alerts: { buffer_min: 7, watch_min: 25 }
        """;

    [Fact]
    public void Alert_settings_in_the_file_override_defaults()
    {
        var s = ScenarioLoader.Parse(Minimal);
        Assert.Equal(7, s.Alerts.BufferMin);
        Assert.Equal(25, s.Alerts.WatchMin);
        Assert.Equal(15, s.Alerts.WarningMin); // not in the file: default kept
    }

    [Fact]
    public void Fixes_inside_a_comms_gap_are_dropped()
    {
        var crew = ScenarioLoader.Parse(Minimal).Crews.Single();
        Assert.Equal([0.0, 1.0, 2.0, 6.0, 7.0, 8.0, 9.0, 10.0], crew.Fixes.Select(f => f.T));
    }

    [Fact]
    public void Route_that_does_not_end_in_its_safety_zone_is_rejected()
    {
        var bad = Minimal.Replace("[-113.61, 37.36, 1520]", "[-113.615, 37.36, 1520]");
        var ex = Assert.Throws<InvalidDataException>(() => ScenarioLoader.Parse(bad));
        Assert.Contains("must end inside safety zone", ex.Message);
    }
}
