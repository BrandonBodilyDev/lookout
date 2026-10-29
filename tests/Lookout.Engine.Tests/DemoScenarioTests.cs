using Lookout.Engine.Rules;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Simulation;

namespace Lookout.Engine.Tests;

/// <summary>
/// The demo scenario is the product's acceptance test: a 90-degree wind shift at minute 20 must
/// produce a cut-off alert in time for Crew Alpha to walk out, about 8 minutes before the fire
/// reaches their route.
/// </summary>
public class DemoScenarioTests
{
    private static readonly Lazy<SimulationResult> Result = new(() =>
        Simulator.Run(ScenarioLoader.LoadFile(Path.Combine(RepoRoot(), "scenarios", "pine-valley-wind-shift.yaml"))));

    [Fact]
    public void Alpha_gets_no_cutoff_alert_before_the_wind_shift()
    {
        Assert.DoesNotContain(Result.Value.Alerts, a => a.CrewId == "ALPHA" && a.T < 20);
    }

    [Fact]
    public void Alpha_goes_critical_at_the_shift_about_8_minutes_before_the_route_is_cut()
    {
        var first = Result.Value.Alerts.First(a => a.CrewId == "ALPHA" && a.To >= AlertLevel.Warning);
        Assert.Equal(20.0, first.T);
        Assert.Equal(AlertLevel.Critical, first.To);
        Assert.Contains("LEAVE NOW via ER-1 to SZ-1", first.Text);

        var cut = Result.Value.Routes.Single(r => r.RouteId == "ER-1").TruthCutMin;
        Assert.NotNull(cut);
        Assert.InRange(cut.Value - first.T, 6.0, 10.0);
    }

    [Fact]
    public void Alpha_reaches_the_safety_zone_without_burnover()
    {
        var alpha = Result.Value.Crews.Single(c => c.CrewId == "ALPHA");
        Assert.Null(alpha.BurnoverMin);
        Assert.NotNull(alpha.ReachedSafetyZoneMin);
    }

    [Fact]
    public void Bravo_comms_go_stale_then_lost_then_recover()
    {
        var bravo = Result.Value.Alerts.Where(a => a.CrewId == "BRAVO").ToList();
        Assert.Collection(bravo,
            a => { Assert.Equal(7.0, a.T); Assert.Equal(AlertLevel.Watch, a.To); },
            a => { Assert.Equal(17.0, a.T); Assert.Equal(AlertLevel.Warning, a.To); Assert.Contains("COMMS LOST", a.Text); },
            a => { Assert.Equal(19.0, a.T); Assert.Equal(AlertLevel.None, a.To); });
    }

    [Fact]
    public void Every_alert_fits_the_270_byte_downlink()
    {
        Assert.All(Result.Value.Alerts, a => Assert.True(a.Bytes <= 270, $"{a.Bytes} B: {a.Text}"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lookout.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repo root (Lookout.slnx) not found.");
    }
}
