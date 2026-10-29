using System.Collections.Concurrent;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Simulation;

namespace Lookout.Api;

/// <summary>Loads scenario YAML files from disk and caches each simulation run (runs are deterministic).</summary>
public sealed class ScenarioStore(string directory)
{
    private readonly ConcurrentDictionary<string, Lazy<SimulationResult>> _runs = new();
    private readonly ConcurrentDictionary<string, Lazy<Scenario>> _scenarios = new();

    public string Directory { get; } = directory;

    public IEnumerable<Scenario> List() =>
        System.IO.Directory.EnumerateFiles(Directory, "*.yaml")
            .Select(p => ScenarioLoader.LoadFile(p, loadTerrain: false))
            .OrderBy(s => s.Name);

    /// <summary>A scenario with its terrain loaded, or null if no such file.</summary>
    public Scenario? Load(string id)
    {
        var path = Path.Combine(Directory, $"{id}.yaml");
        if (!id.All(ch => char.IsLetterOrDigit(ch) || ch == '-') || !File.Exists(path)) return null;
        return _scenarios.GetOrAdd(id, _ => new Lazy<Scenario>(() => ScenarioLoader.LoadFile(path))).Value;
    }

    public SimulationResult? Run(string id)
    {
        var path = Path.Combine(Directory, $"{id}.yaml");
        if (!id.All(ch => char.IsLetterOrDigit(ch) || ch == '-') || !File.Exists(path)) return null;
        return _runs.GetOrAdd(id, _ => new Lazy<SimulationResult>(() => Simulator.Run(Load(id)!))).Value;
    }

    /// <summary>Scenario folder: the configured path, else the repo's scenarios/ found by walking up from the app.</summary>
    public static string Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        foreach (var start in new[] { AppContext.BaseDirectory, System.IO.Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "scenarios");
                if (System.IO.Directory.Exists(candidate)) return candidate;
            }
        }
        throw new DirectoryNotFoundException("No scenarios directory found. Set Lookout__ScenarioDir.");
    }
}
