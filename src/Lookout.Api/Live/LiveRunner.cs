using System.Text.Json;
using System.Threading.Channels;
using System.Xml.Linq;
using Lookout.Api.Data;
using Lookout.Api.Tak;
using Lookout.Api.Weather;
using Lookout.Engine.Rules;
using Lookout.Engine.Spread;
using Lookout.Link;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Lookout.Api.Live;

/// <summary>The MQTT broker, or null for loopback mode (no broker; alerts never leave the server).</summary>
public sealed record BrokerConfig(MqttSettings? Settings);

public sealed class LiveOptions
{
    /// <summary>How often the engine re-evaluates every crew, in real seconds.</summary>
    public double TickSeconds { get; set; } = 1;
    /// <summary>Scenario minutes a WARNING or CRITICAL may wait for acknowledgement before escalating to the IC.</summary>
    public double AckTimeoutMin { get; set; } = 3;
    /// <summary>Close an incident this many scenario minutes after its scenario ends.</summary>
    public double CloseAfterMin { get; set; } = 5;
    public double MinSpacingSec { get; set; } = 40;
    public double RepeatSec { get; set; } = 60;
}

/// <summary>
/// The live engine. One loop, one thread of control: it drains MQTT messages and API signals into
/// the per-incident runtimes, then every tick runs the rules for every crew, raises and escalates
/// alerts, lets the link governor pick what to transmit, and pushes a frame to dashboards.
/// Keeping all mutation on this loop is what lets <see cref="IncidentRuntime"/> skip locking.
/// </summary>
public sealed partial class LiveRunner(
    IServiceScopeFactory scopes,
    ScenarioStore scenarios,
    IncidentSignals signals,
    IHubContext<IncidentHub> hub,
    LiveSnapshots snapshots,
    DeviceActivity activity,
    LiveHealth health,
    NwsClient nws,
    CotSender cot,
    Push.PushNotifier push,
    CotSettings cotSettings,
    LiveOptions options,
    BrokerConfig broker,
    TimeProvider clock,
    ILogger<LiveRunner> log) : BackgroundService
{
    private readonly Dictionary<Guid, IncidentRuntime> _incidents = new();
    /// <summary>MQTT messages, each with the device directory as it was when the message arrived.</summary>
    private readonly Channel<(string Topic, byte[] Payload, IReadOnlyDictionary<string, (Guid Incident, string Crew)>? Devices)> _inbox =
        Channel.CreateUnbounded<(string, byte[], IReadOnlyDictionary<string, (Guid Incident, string Crew)>?)>();
    private readonly Dictionary<Guid, DateTimeOffset> _nwsFetched = new();
    private DateTimeOffset _nextCot;
    private DateTimeOffset _nextOverrunLog;
    private MqttConnection? _mqtt;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Before subscribing, so the first uplinks are not dropped as unregistered.
        await Guard("loading the device directory", () => ReloadDevicesAsync(ct), ct);
        if (broker.Settings is { } mqttSettings)
        {
            _mqtt = new MqttConnection(mqttSettings, "lookout-api", m => log.LogInformation("{Message}", m))
            {
                // Resolve the device's owner as of arrival: a reassignment processed later in the same
                // pass must not credit this message (a fix, or an ack by link sequence) to the new crew.
                OnMessage = (topic, payload) => { _inbox.Writer.TryWrite((topic, payload, _devicesLoaded ? _devices : null)); return Task.CompletedTask; },
            };
            health.BrokerConnected = () => _mqtt.IsConnected;
            await _mqtt.StartAsync([Topics.AllDeviceUplinks, Topics.AllDroneFronts, Topics.AllWeather], ct);
        }
        else
        {
            log.LogWarning("No MQTT broker configured: alerts are marked sent without leaving the server (loopback mode).");
        }

        await LoadRunningIncidentsAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.TickSeconds));
        do
        {
            // Each unit of work is guarded on its own: one bad message or one failing incident must
            // never stop the others, because a stalled loop means no alerts for anyone.
            var passStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var messages = 0;
            while (signals.Reader.TryRead(out var s)) await Guard("signal", () => HandleSignalAsync(s, ct), ct);
            if (!_devicesLoaded || clock.GetUtcNow() >= _nextDeviceReload) await Guard("loading the device directory", () => ReloadDevicesAsync(ct), ct);
            while (_inbox.Reader.TryRead(out var m))
            {
                messages++;
                await Guard($"message on {m.Topic}", () => HandleMessageAsync(m.Topic, m.Payload, m.Devices, ct), ct);
            }
            foreach (var rt in _incidents.Values.ToList()) await Guard($"tick of incident {rt.Incident.Id}", () => TickAsync(rt, ct), ct);
            await Guard("CoT export", () => ExportCotAsync(ct), ct);
            var passMs = System.Diagnostics.Stopwatch.GetElapsedTime(passStarted).TotalMilliseconds;
            Telemetry.LoopDuration.Record(passMs);
            Telemetry.LoopMessages.Record(messages);
            health.Ticked(clock.GetUtcNow(), passMs);
            // A pass longer than the tick means alerts go out late; say so, but not on every pass.
            if (passMs > options.TickSeconds * 1000 && clock.GetUtcNow() >= _nextOverrunLog)
            {
                log.LogWarning("Live loop pass took {Ms:0} ms, longer than the {Tick} s tick ({Messages} messages, {Incidents} incidents); alerts are running late",
                    passMs, options.TickSeconds, messages, _incidents.Count);
                _nextOverrunLog = clock.GetUtcNow().AddSeconds(30);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task Guard(string what, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogError(e, "Live runner: {What} failed; continuing", what);
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        if (_mqtt is not null) await _mqtt.DisposeAsync();
        await base.StopAsync(ct);
    }

    private async Task LoadRunningIncidentsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        var running = await db.Incidents.AsNoTracking().Where(i => i.Status == IncidentStatus.Running).Select(i => i.Id).ToListAsync(ct);
        // One incident whose scenario cannot be loaded (a bad or newer-format file) must not stop the
        // runner for every other incident; it found its way into a real restart test.
        foreach (var id in running) await Guard($"loading incident {id}", () => LoadAsync(id, ct), ct);
    }

    private async Task<IncidentRuntime?> LoadAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        var incident = await db.Incidents.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id && i.Status == IncidentStatus.Running, ct);
        if (incident is null) return null;
        var scenario = scenarios.Load(incident.ScenarioId);
        if (scenario is null)
        {
            log.LogWarning("Incident {Id} references missing scenario {Scenario}", id, incident.ScenarioId);
            return null;
        }
        var rt = new IncidentRuntime(incident, scenario, new GovernorSettings(options.MinSpacingSec, options.RepeatSec));
        await ReloadLayoutAsync(rt, db, ct);

        // Restart: pick up where the last run left off, so a restart does not look like silence
        // (no fixes, every crew at None) and send everyone false comms and cut-off warnings.
        foreach (var crew in rt.Crews)
        {
            var lastFix = await db.CrewFixes.AsNoTracking()
                .Where(f => f.IncidentId == id && f.CrewId == crew.CrewId)
                .OrderByDescending(f => f.Time).FirstOrDefaultAsync(ct);
            if (lastFix is not null)
                rt.Fixes[crew.CrewId] = (lastFix.ScenarioMin, rt.Frame.ToLocal(lastFix.Position.X, lastFix.Position.Y), -1);
            var lastAlert = await db.Alerts.AsNoTracking()
                .Where(a => a.IncidentId == id && a.CrewId == crew.CrewId)
                .OrderByDescending(a => a.LinkSeq).FirstOrDefaultAsync(ct);
            if (lastAlert is not null && rt.Monitors.TryGetValue(crew.CrewId, out var monitor))
                monitor.Seed(lastAlert.Level);
        }

        // Re-offer only what still needs the link: unsent alerts, and warnings awaiting acknowledgement.
        var open = await db.Alerts.AsNoTracking()
            .Where(a => a.IncidentId == id && (a.State == Engine.Workflow.AlertState.Queued
                || (a.RequiresAck && (a.State == Engine.Workflow.AlertState.Sent || a.State == Engine.Workflow.AlertState.Escalated))))
            .ToListAsync(ct);
        foreach (var a in open) Offer(rt, a, leaveByMin: null);

        _incidents[id] = rt;
        log.LogInformation("Live incident {Id} ({Name}) loaded at scenario minute {T:0.0}", id, incident.Name, rt.ScenarioMinute(clock.GetUtcNow()));
        return rt;
    }

    private static async Task ReloadLayoutAsync(IncidentRuntime rt, LookoutDb db, CancellationToken ct)
    {
        var id = rt.Incident.Id;
        rt.LoadLayout(
            await db.Crews.AsNoTracking().Where(c => c.IncidentId == id).ToListAsync(ct),
            await db.EscapeRoutes.AsNoTracking().Where(r => r.IncidentId == id).ToListAsync(ct),
            await db.SafetyZones.AsNoTracking().Where(z => z.IncidentId == id).ToListAsync(ct));
    }

    private async Task HandleSignalAsync(IncidentSignal s, CancellationToken ct)
    {
        switch (s)
        {
            case IncidentCreated when !_incidents.ContainsKey(s.IncidentId):
                // A field message may already have loaded it; reloading would reset its front and wind.
                await LoadAsync(s.IncidentId, ct);
                break;
            case LayoutChanged when _incidents.TryGetValue(s.IncidentId, out var rt):
                await using (var scope = scopes.CreateAsyncScope())
                    await ReloadLayoutAsync(rt, scope.ServiceProvider.GetRequiredService<LookoutDb>(), ct);
                break;
            case AlertClosed c when _incidents.TryGetValue(s.IncidentId, out var rt2):
                rt2.Governor.Clear(c.CrewId, c.AlertId);
                break;
            case DevicesChanged:
                await ReloadDevicesAsync(ct);
                await hub.Clients.Group(s.IncidentId.ToString()).SendAsync("devices", ct);
                break;
            case UplinkReceived u when _incidents.TryGetValue(s.IncidentId, out var rt3):
                await HandleUplinkAsync(rt3, u.CrewId, u.Payload, ct);
                break;
            case NwsWindReceived w when _incidents.TryGetValue(s.IncidentId, out var rt4):
                rt4.UpdateWind(rt4.Wind with { WindFromDeg = w.WindFromDeg ?? rt4.Wind.WindFromDeg, MidflameWindMs = w.MidflameWindMs }, w.Label);
                break;
        }
    }

    private async Task TickAsync(IncidentRuntime rt, CancellationToken ct)
    {
        using var activity = Telemetry.Source.StartActivity("engine.tick");
        activity?.SetTag("lookout.incident", rt.Incident.Id.ToString());
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await TickCoreAsync(rt, ct);
        }
        finally
        {
            Telemetry.TickDuration.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private async Task TickCoreAsync(IncidentRuntime rt, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var t = rt.ScenarioMinute(now);
        if (t > rt.Scenario.DurationMin + options.CloseAfterMin)
        {
            await CloseAsync(rt, ct);
            return;
        }
        if (rt.Incident.WindSource == WindSource.Nws) await RefreshNwsAsync(rt, now, ct);

        await using var scope = scopes.CreateAsyncScope();
        var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
        var group = hub.Clients.Group(rt.Incident.Id.ToString());

        // Assess every crew in parallel: each crew has its own monitor, and what they share (terrain,
        // spread grid, the front's cached cells) is read-only. Saving, sending and pushing stay
        // sequential below, in crew order, on the loop.
        var work = new List<(CrewRecord Crew, List<RouteOption> Options, Coordinate Known, double? FixMin)>();
        foreach (var crew in rt.Crews)
        {
            if (!rt.Routes.TryGetValue(crew.CrewId, out var options)) continue;
            var hasFix = rt.Fixes.TryGetValue(crew.CrewId, out var fix);
            // Give devices a few minutes to report in before treating silence as a comms problem.
            if (!hasFix && t < rt.Scenario.Alerts.CommsStaleMin) continue;
            work.Add((crew, options, hasFix ? fix.Position : options[0].Profile.Samples[0].Point, hasFix ? fix.ScenarioMin : null));
        }
        var (front, wind) = (rt.Front, rt.Wind);
        var assessed = new CrewAssessment[work.Count];
        Parallel.For(0, work.Count, new ParallelOptions { CancellationToken = ct }, i =>
            assessed[i] = rt.Monitors[work[i].Crew.CrewId].Assess(work[i].Options, work[i].Known, work[i].FixMin, front, wind, t, rt.ClockLabel));

        for (var i = 0; i < work.Count; i++)
        {
            var crew = work[i].Crew;
            var a = assessed[i];
            rt.LastAssessment[crew.CrewId] = a;
            if (a.Change is not { } change) continue;

            RaiseResult raised;
            try
            {
                raised = await alerts.RaiseAsync(rt.Incident.Id, crew.CrewId, change.To, change.Text, t, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Not recorded means not sent: roll the latch back so the next tick raises it again.
                rt.Monitors[crew.CrewId].Revert(change);
                log.LogError(e, "Could not raise {Level} for crew {Crew}; will retry next tick", change.To, crew.CrewId);
                continue;
            }
            Telemetry.AlertsRaised.Add(1, new KeyValuePair<string, object?>("level", change.To.ToString().ToLowerInvariant()));
            Offer(rt, raised.Alert, a.Cutoff.LeaveByMin);
            // Wake the crew's phones beside the link, not after it: push does not wait for the broker or
            // the satellite session spacing. Raising happens once per alert, so a restart cannot push twice.
            if (push.Enabled && raised.Alert.RequiresAck)
            {
                var (incidentId, crewId, level, text) = (raised.Alert.IncidentId, raised.Alert.CrewId, raised.Alert.Level, raised.Alert.Text);
                _ = Task.Run(async () =>
                {
                    try { await push.NotifyAsync(incidentId, crewId, level, text, ct); }
                    catch (Exception e) when (!ct.IsCancellationRequested) { log.LogWarning(e, "Web Push for crew {Crew} failed", crewId); }
                }, ct);
            }
            foreach (var old in raised.Superseded) await group.SendAsync("alert", AlertDto.From(old), ct);
            await group.SendAsync("alert", AlertDto.From(raised.Alert), ct);
        }

        var timeout = TimeSpan.FromMinutes(options.AckTimeoutMin / rt.Incident.ClockRate);
        foreach (var escalated in await alerts.EscalateOverdueAsync(rt.Incident.Id, timeout, ct))
        {
            Telemetry.AlertsEscalated.Add(1);
            await group.SendAsync("alert", AlertDto.From(escalated), ct);
        }

        foreach (var tx in rt.Governor.Due(t * 60))
        {
            if (_mqtt is not null)
            {
                // Every device the crew carries gets it; a crew with no device has no link, so the alert
                // stays queued and is retried; if it needs an ack it escalates to the IC after the ack timeout.
                var devices = DevicesOf(rt.Incident.Id, tx.CrewId).ToList();
                var sentToAny = false;
                foreach (var device in devices)
                    sentToAny |= await _mqtt.TryPublishAsync(Topics.DeviceDownlink(device), tx.Payload, ct);
                if (!sentToAny)
                {
                    rt.Governor.Failed(tx); // broker unreachable or no device: due again next tick
                    if (devices.Count > 0) Telemetry.DownlinkFailures.Add(1); // count broker failures, not crews without a device
                    continue;
                }
            }
            Telemetry.DownlinkBytes.Add(tx.Payload.Length);
            rt.Governor.Delivered(tx);
            try
            {
                await alerts.MarkSentAsync(tx.AlertId, ct);
                var sent = await scope.ServiceProvider.GetRequiredService<LookoutDb>().Alerts.AsNoTracking().SingleAsync(x => x.Id == tx.AlertId, ct);
                await group.SendAsync("alert", AlertDto.From(sent), ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // The message did go out; only the bookkeeping failed (for example the IC cancelled it
                // in the same instant). Log it and carry on with the other transmissions.
                log.LogWarning(e, "Sent alert {Alert} but could not record it", tx.AlertId);
                scope.ServiceProvider.GetRequiredService<LookoutDb>().ChangeTracker.Clear();
            }
        }

        var frame = LiveFrame.Build(rt, t, now);
        snapshots.Set(rt.Incident.Id, frame);
        await group.SendAsync("frame", frame, ct);
    }

    private void Offer(IncidentRuntime rt, AlertRecord alert, double? leaveByMin)
    {
        if (alert.State is not (Engine.Workflow.AlertState.Queued or Engine.Workflow.AlertState.Sent or Engine.Workflow.AlertState.Escalated)) return;
        ushort? leaveBy = null;
        if (leaveByMin is { } lb && double.IsFinite(lb) && alert.Level >= AlertLevel.Warning)
        {
            var local = rt.Scenario.Start.AddMinutes(lb);
            leaveBy = (ushort)(local.Hour * 60 + local.Minute);
        }
        var payload = WireFormat.EncodeAlert(new AlertMessage((ushort)alert.LinkSeq, (byte)alert.Level, leaveBy, alert.Text));
        rt.Governor.Offer(alert.CrewId, alert.Id, (int)alert.Level, payload, alert.RequiresAck);
    }

    private async Task CloseAsync(IncidentRuntime rt, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        var incident = await db.Incidents.SingleAsync(i => i.Id == rt.Incident.Id, ct);
        incident.Status = IncidentStatus.Closed;
        db.AuditEvents.Add(new AuditEvent
        {
            Time = clock.GetUtcNow(), IncidentId = incident.Id, Type = "incident.closed", Actor = "lookout-engine", Role = "system",
            Details = JsonSerializer.SerializeToDocument(new { reason = "scenario ended" }),
        });
        await db.SaveChangesAsync(ct);
        _incidents.Remove(rt.Incident.Id);
        await hub.Clients.Group(incident.Id.ToString()).SendAsync("closed", new { incidentId = incident.Id }, ct);
        log.LogInformation("Incident {Id} closed", incident.Id);
    }
}
