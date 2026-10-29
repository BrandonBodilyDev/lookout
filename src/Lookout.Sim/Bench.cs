using System.Diagnostics;
using Lookout.Engine.Rules;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;

namespace Lookout.Sim;

/// <summary>
/// How many crews can one incident's loop evaluate at 1 Hz? Clones Crew Alpha (every route) N times
/// and times one engine tick at the moment of the demo's wind shift, when every crew is threatened.
/// </summary>
public static class Bench
{
    public static void Run(Scenario scenario, int[] crewCounts)
    {
        var grid = new SpreadGrid(scenario.Terrain ?? throw new InvalidOperationException("Benchmark needs a terrain scenario."));
        var alpha = scenario.Crews.Single(c => c.Id == "ALPHA");
        var options = alpha.AllRouteIds.Select(id => scenario.Route(id))
            .Select(r => new RouteOption(new RouteProfile(r, alpha.Pace), scenario.SafetyZone(r.SafetyZoneId))).ToList();
        const double t = 20;
        var front = scenario.Fire.FrontAt(t);
        var wind = WindPhases.At(scenario.Wind, t).Conditions;
        var known = alpha.TruePositionAt(t);

        Console.WriteLine($"Terrain {scenario.Terrain.Nx} x {scenario.Terrain.Ny} cells at {scenario.Terrain.CellSize} m; {options.Count} routes per crew; scenario minute {t}.");
        Console.WriteLine($"crews   tick ms (median of 5)   per crew ms   parallel tick ms ({Environment.ProcessorCount} cores)");
        foreach (var n in crewCounts)
        {
            var monitors = Enumerable.Range(0, n).Select(i => new CrewMonitor($"C{i}", scenario.Alerts, grid)).ToList();
            var samples = new List<double>();
            for (var rep = 0; rep < 5; rep++)
            {
                var sw = Stopwatch.StartNew();
                foreach (var m in monitors) m.Assess(options, known, t, front, wind, t, _ => "--:--");
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            var median = samples.Order().ElementAt(2);
            var parallel = new List<double>();
            for (var rep = 0; rep < 5; rep++)
            {
                var sw = Stopwatch.StartNew();
                Parallel.ForEach(monitors, m => m.Assess(options, known, t, front, wind, t, _ => "--:--"));
                parallel.Add(sw.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"{n,5}   {median,18:0.0}   {median / n,13:0.00}   {parallel.Order().ElementAt(2),16:0.0}");
        }
    }
}
