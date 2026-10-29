using System.Net.Http.Json;
using System.Text.Json;
using Lookout.Drone;
using Lookout.Engine.Geo;
using Lookout.Engine.Scenarios;
using Lookout.Engine.Spread;
using Lookout.Link;
using NetTopologySuite.Geometries;

// Lookout field simulator: plays a scenario's truth over MQTT as the field would.
//   crew devices  -> 16-byte GPS fixes on the uplink, 8-byte acks for WARNING/CRITICAL alerts
//   drone         -> burned-area polygons every 30 scenario seconds (broadband link, JSON)
//                    DRONE_MODE=truth (default) sends the scenario's fire; DRONE_MODE=camera photographs it
//                    with a virtual survey camera, runs the colour detector, and sends what was detected
//   weather       -> wind and head-fire rate every scenario minute and at each shift
// Crew devices in a comms gap neither send nor receive. Configuration is by environment variable.

string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

var apiBase = Env("LOOKOUT_API", "http://localhost:5080").TrimEnd('/');
var scenarioId = Env("SCENARIO", "pine-valley-wind-shift");
var scenarioDir = Env("SCENARIO_DIR", FindScenarioDir());
var clockRate = double.Parse(Env("CLOCK_RATE", "4"), System.Globalization.CultureInfo.InvariantCulture);
var ackDelay = TimeSpan.FromSeconds(double.Parse(Env("ACK_DELAY_SEC", "8"), System.Globalization.CultureInfo.InvariantCulture));
var noAck = Env("NO_ACK_CREWS", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
var droneMode = Env("DRONE_MODE", "truth").ToLowerInvariant();
var surveyAltitude = double.Parse(Env("SURVEY_ALTITUDE_M", "1800"), System.Globalization.CultureInfo.InvariantCulture);
var mqtt = new MqttSettings
{
    Host = Env("MQTT_HOST", "localhost"),
    Port = int.Parse(Env("MQTT_PORT", "1883")),
    Username = Environment.GetEnvironmentVariable("MQTT_USER"),
    Password = Environment.GetEnvironmentVariable("MQTT_PASSWORD"),
};

var scenario = ScenarioLoader.LoadFile(Path.Combine(scenarioDir, $"{scenarioId}.yaml"));
var frame = scenario.Frame;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

// 1. Find or create the incident through the API (dev sign-in as the IC).
using var http = new HttpClient { BaseAddress = new Uri(apiBase) };
var (incidentId, startedAt, rate) = await ConnectIncidentAsync();
Log($"Incident {incidentId}: {scenario.Name}, {rate}x real time, started {startedAt:HH:mm:ss}Z");

// Each crew carries one simulated device. A real one would be a satellite messenger registered by its
// IMEI; here the id is derived from the incident so separate runs never share a device.
var deviceOf = scenario.Crews.ToDictionary(c => c.Id, c => $"SIM-{incidentId:N}"[..12] + $"-{c.Id}");
var crewOf = deviceOf.ToDictionary(kv => kv.Value, kv => kv.Key);
foreach (var (crewId, deviceId) in deviceOf)
{
    var res = await http.PutAsync($"/api/incidents/{incidentId}/crews/{crewId}/devices/{deviceId}", null, ct);
    res.EnsureSuccessStatusCode();
}
Log($"Registered devices: {string.Join(", ", deviceOf.Values)}");

// 2. MQTT: listen on every crew's downlink.
await using var link = new MqttConnection(mqtt, "lookout-field", Log);
var pendingAcks = new List<(DateTimeOffset Due, string Crew, ushort Seq)>();
var gate = new object();
link.OnMessage = (topic, payload) =>
{
    if (Topics.ParseDevice(topic) is not { Up: false } dev || !crewOf.TryGetValue(dev.DeviceId, out var crewId)) return Task.CompletedTask;
    var crew = scenario.Crews.Single(c => c.Id == crewId);
    var t = Now();
    if (crew.InCommsGap(t))
    {
        Log($"{crewId} device unreachable (comms gap): alert dropped on the link");
        return Task.CompletedTask;
    }
    var alert = WireFormat.DecodeAlert(payload);
    Log($"{crewId} device received #{alert.Sequence} ({payload.Length} B): {alert.Text}");
    if (alert.Level >= 2 && !noAck.Contains(crewId))
        lock (gate) pendingAcks.Add((DateTimeOffset.UtcNow + ackDelay, crewId, alert.Sequence));
    return Task.CompletedTask;
};
await link.StartAsync(deviceOf.Values.Select(Topics.DeviceDownlink), ct);

// Camera mode: a nadir survey camera parked over the incident area. At 1800 m above ground with a
// 71.5 degree lens its footprint is about 2.6 x 1.9 km. That is an incident-awareness aircraft or a
// Group 3 UAS, not a Mavic Mini, whose 120 m ceiling gives a footprint near 170 m.
SurveyRig? rig = null;
if (droneMode == "camera")
{
    if (scenario.Terrain is null) throw new InvalidOperationException("Camera mode needs a scenario with terrain.");
    var pts = scenario.Routes.SelectMany(r => r.Vertices).Select(v => new Coordinate(v.X, v.Y)).ToList();
    var centre = new Coordinate((pts.Average(p => p.X) + scenario.Fire.FrontAt(0).Centroid.X) / 2, (pts.Average(p => p.Y) + scenario.Fire.FrontAt(0).Centroid.Y) / 2 - 300);
    var camera = CameraModel.WithAspect(800, 600, 71.5);
    var projector = new GroundProjector(camera, scenario.Terrain);
    rig = new SurveyRig(new SyntheticCamera(projector), new ColorFireDetector(), new FrontMapper(scenario.Terrain, projector),
        new CameraPose(centre, scenario.Terrain.Elevation(centre.X, centre.Y) + surveyAltitude, 0, -90));
    Log($"Drone mode: virtual survey camera {surveyAltitude:0} m above ground, colour detector");
}
Task? surveyInFlight = null;

// 3. Play the scenario.
var published = scenario.Crews.ToDictionary(c => c.Id, _ => -1);
var seq = scenario.Crews.ToDictionary(c => c.Id, _ => (ushort)0);
var lastSurvey = -1;
var lastWeatherMinute = -1;
WindPhase? lastPhase = null;

while (!ct.IsCancellationRequested)
{
    var t = Now();
    if (t > scenario.DurationMin + 1) break;
    if (!link.IsConnected) { await Task.Delay(500, ct); continue; }

    foreach (var crew in scenario.Crews)
    {
        for (var i = published[crew.Id] + 1; i < crew.Fixes.Count && crew.Fixes[i].T <= t; i++)
        {
            var (fixT, pos) = crew.Fixes[i];
            var (lon, lat) = frame.ToLonLat(pos);
            var report = new PositionReport(++seq[crew.Id] , startedAt.AddMinutes(fixT / rate), lat, lon, (int)Math.Max(5, 100 - fixT / 2));
            if (!await link.TryPublishAsync(Topics.DeviceUplink(deviceOf[crew.Id]), WireFormat.EncodePosition(report), ct)) break;
            published[crew.Id] = i;
        }
    }

    var survey = (int)Math.Floor(t / scenario.StepMin);
    if (survey > lastSurvey && surveyInFlight is not { IsCompleted: false })
    {
        var surveyMin = survey * scenario.StepMin;
        var taken = startedAt.AddMinutes(surveyMin / rate);
        var truth = scenario.Fire.FrontAt(surveyMin);
        lastSurvey = survey;
        if (rig is null)
        {
            var msg = new DroneFrontMessage(taken, MultiPolygon(truth), "field-sim drone survey (scenario truth)");
            await link.TryPublishAsync(Topics.DroneFront(incidentId), SurveyJson.Encode(msg), ct);
        }
        else
        {
            // Rendering and detection take a second or two; keep crew fixes flowing meanwhile.
            surveyInFlight = Task.Run(async () =>
            {
                var detected = rig.Survey(truth, survey);
                var msg = new DroneFrontMessage(taken, MultiPolygon(detected), "virtual survey camera + colour detector");
                await link.TryPublishAsync(Topics.DroneFront(incidentId), SurveyJson.Encode(msg), ct);
            }, ct);
        }
    }

    var phase = WindPhases.At(scenario.Wind, t);
    if ((int)t > lastWeatherMinute || !ReferenceEquals(phase, lastPhase))
    {
        var c = phase.Conditions;
        var wx = new WeatherMessage(DateTimeOffset.UtcNow, c.WindFromDeg, c.MidflameWindMs, c.HeadRateMPerMin, "field-sim weather station");
        await link.TryPublishAsync(Topics.Weather(incidentId), SurveyJson.Encode(wx), ct);
        if (!ReferenceEquals(phase, lastPhase) && lastPhase is not null) Log($"Wind now from {c.WindFromDeg:0}° at {c.MidflameWindMs} m/s");
        lastWeatherMinute = (int)t;
        lastPhase = phase;
    }

    List<(DateTimeOffset Due, string Crew, ushort Seq)> due;
    lock (gate)
    {
        due = pendingAcks.Where(a => a.Due <= DateTimeOffset.UtcNow).ToList();
        pendingAcks.RemoveAll(a => a.Due <= DateTimeOffset.UtcNow);
    }
    foreach (var a in due)
    {
        if (scenario.Crews.Single(c => c.Id == a.Crew).InCommsGap(t)) { Log($"{a.Crew} ack #{a.Seq} lost (comms gap)"); continue; }
        await link.TryPublishAsync(Topics.DeviceUplink(deviceOf[a.Crew]), WireFormat.EncodeAck(new AckReport(a.Seq, DateTimeOffset.UtcNow)), ct);
        Log($"{a.Crew} acknowledged #{a.Seq}");
    }

    await Task.Delay(250, ct);
}
Log("Scenario finished.");
return;

double Now() => (DateTimeOffset.UtcNow - startedAt).TotalMinutes * rate;

void Log(string m) => Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {m}");

double[][][][] MultiPolygon(Geometry g) =>
    g.IsEmpty ? [] : Enumerable.Range(0, g.NumGeometries).Select(g.GetGeometryN).OfType<Polygon>()
        .Select(p => new[] { p.ExteriorRing.Coordinates.Select(c => { var (lon, lat) = frame.ToLonLat(c); return new[] { Math.Round(lon, 6), Math.Round(lat, 6) }; }).ToArray() })
        .ToArray();

async Task<(Guid, DateTimeOffset, double)> ConnectIncidentAsync()
{
    for (var attempt = 0; ; attempt++)
    {
        try
        {
            var tokenRes = await http.PostAsJsonAsync("/api/dev/token", new { role = "ic", name = "field-sim" }, ct);
            tokenRes.EnsureSuccessStatusCode();
            var token = (await tokenRes.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("token").GetString();
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);

            if (Environment.GetEnvironmentVariable("INCIDENT") is { Length: > 0 } existing)
            {
                var inc = await http.GetFromJsonAsync<JsonElement>($"/api/incidents/{existing}", ct);
                return (Guid.Parse(existing), inc.GetProperty("startedAt").GetDateTimeOffset(), inc.GetProperty("clockRate").GetDouble());
            }
            var created = await http.PostAsJsonAsync("/api/incidents", new { scenarioId, clockRate }, ct);
            created.EnsureSuccessStatusCode();
            var body = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
            return (body.GetProperty("id").GetGuid(), body.GetProperty("startedAt").GetDateTimeOffset(), body.GetProperty("clockRate").GetDouble());
        }
        catch (HttpRequestException e) when (attempt < 30)
        {
            Log($"API not ready ({e.Message}); retrying");
            await Task.Delay(2000, ct);
        }
    }
}

static string FindScenarioDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "scenarios"))) return Path.Combine(dir.FullName, "scenarios");
    return "scenarios";
}
