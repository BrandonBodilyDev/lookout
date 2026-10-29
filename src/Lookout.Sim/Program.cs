using Lookout.Engine.Scenarios;
using Lookout.Engine.Simulation;

using System.Globalization;
using Lookout.Sim;

// Lookout scenario runner.
//   Run a scenario:   dotnet run --project src/Lookout.Sim -- scenarios/pine-valley-wind-shift.yaml
//   Build terrain:    dotnet run --project src/Lookout.Sim -- dem <scenario.yaml> <out.asc> <west> <east> <south> <north> [cell]
//                     (extent in metres in the scenario's local frame, east and north of its origin)
//   Drone survey:     dotnet run --project src/Lookout.Sim -- drone (track | survey | flight) ...
if (args.Length > 0 && args[0] == "vapid")
{
    // Web Push identity for the API: Lookout__WebPush__PublicKey / Lookout__WebPush__PrivateKey.
    using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    var p = ec.ExportParameters(true);
    string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    Console.WriteLine($"Lookout__WebPush__PublicKey={B64([0x04, .. p.Q.X!, .. p.Q.Y!])}");
    Console.WriteLine($"Lookout__WebPush__PrivateKey={B64(p.D!)}");
    return 0;
}

if (args.Length > 0 && args[0] == "drone")
    return await DroneCommands.RunAsync(args);

if (args.Length > 0 && args[0] == "dem")
{
    double D(int i) => double.Parse(args[i], CultureInfo.InvariantCulture);
    var target = ScenarioLoader.LoadFile(args[1], loadTerrain: false);
    var grid = await TerrariumDem.BuildAsync(target.Frame, D(3), D(4), D(5), D(6), args.Length > 7 ? D(7) : 30);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    grid.WriteAscii(args[2]);
    Console.WriteLine($"Wrote {args[2]}: elevation {grid.Z.Min():0} to {grid.Z.Max():0} m.");
    return 0;
}

var path = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : Path.Combine("scenarios", "pine-valley-wind-shift.yaml");
var scenario = ScenarioLoader.LoadFile(path);
// --camera: the engine sees what a virtual survey camera and the colour detector report, not the true fire.
var useCamera = args.Contains("--camera");
var result = Simulator.Run(scenario, useCamera ? CameraSurvey.Observer(scenario) : null);
if (useCamera) Console.WriteLine("Fire front: virtual survey camera at 1800 m + colour detector (not scenario truth)");

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

return 0;

string Fmt(double? m) => m is { } v ? Simulator.Clock(scenario, v) : "never";
