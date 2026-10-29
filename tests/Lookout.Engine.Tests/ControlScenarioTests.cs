using Lookout.Engine.Rules;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Simulation;

namespace Lookout.Engine.Tests;

/// <summary>
/// The false-alarm check. Same fire, terrain and crews as the demo, but the wind never shifts and the
/// fire runs away from both crews. A warning system that cries wolf gets ignored, so this must stay quiet.
/// </summary>
public class ControlScenarioTests
{
    private static readonly Lazy<SimulationResult> Result = new(() =>
        Simulator.Run(ScenarioLoader.LoadFile(Path.Combine(RepoRoot(), "scenarios", "pine-valley-steady-north.yaml"))));

    [Fact]
    public void No_alerts_at_all_when_the_fire_runs_away_from_the_crews()
    {
        Assert.Empty(Result.Value.Alerts);
    }

    [Fact]
    public void Alpha_keeps_a_wide_margin_all_shift()
    {
        var margins = Result.Value.Frames.Select(f => f.Crews.Single(c => c.CrewId == "ALPHA").Cutoff.MarginMin).ToList();
        Assert.All(margins, m => Assert.True(m > 60, $"margin {m:0.0} min"));
        Assert.All(Result.Value.Frames, f => Assert.Equal(AlertLevel.None, f.Crews.Single(c => c.CrewId == "ALPHA").Level));
    }

    [Fact]
    public void The_fire_never_reaches_either_route()
    {
        Assert.All(Result.Value.Routes, r => Assert.Null(r.TruthCutMin));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Lookout.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }
}
