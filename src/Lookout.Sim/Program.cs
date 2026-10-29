using Lookout.Engine.Scenarios;
using Lookout.Engine.Simulation;

// Lookout scenario runner: prints the alert log and the truth scorecard for one scenario.
// Usage: dotnet run --project src/Lookout.Sim -- scenarios/pine-valley-wind-shift.yaml
var path = args.Length > 0 ? args[0] : Path.Combine("scenarios", "pine-valley-wind-shift.yaml");
var scenario = ScenarioLoader.LoadFile(path);
var result = Simulator.Run(scenario);

Console.WriteLine($"{scenario.Name}  ({scenario.Start:yyyy-MM-dd HH:mm}, {scenario.DurationMin} min)");
Console.WriteLine();
Console.WriteLine("Alert log");
foreach (var a in result.Alerts)
    Console.WriteLine($"  {a.Clock:HH:mm:ss}  {a.CrewId,-6} {a.From,-8} -> {a.To,-8} {a.Bytes,4} B  {a.Text}");

Console.WriteLine();
Console.WriteLine("Truth scorecard");
foreach (var r in result.Routes)
    Console.WriteLine($"  {r.RouteId}: fire reaches route at {(r.TruthCutMin is { } c ? Simulator.Clock(scenario, c) : "never")}");
foreach (var c in result.Crews)
{
    var crew = scenario.Crews.Single(x => x.Id == c.CrewId);
    var route = result.Routes.Single(r => r.RouteId == crew.RouteId);
    var firstAlert = result.Alerts.FirstOrDefault(a =>
        a.CrewId == c.CrewId && a.To >= Lookout.Engine.Rules.AlertLevel.Warning && a.Text.Contains("via"));
    var lead = firstAlert is not null && route.TruthCutMin is { } cut ? $"{cut - firstAlert.T:0.0} min before the cut" : "n/a";
    Console.WriteLine($"  {c.CrewId}: reached safety zone {Fmt(c.ReachedSafetyZoneMin)}, burnover {Fmt(c.BurnoverMin)}, first cut-off warning {lead}");
}

string Fmt(double? m) => m is { } v ? Simulator.Clock(scenario, v) : "never";
