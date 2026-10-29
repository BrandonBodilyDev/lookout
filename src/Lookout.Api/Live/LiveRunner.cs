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
public sealed class LiveRunner(
    IServiceScopeFactory scopes,
    ScenarioStore scenarios,
    IncidentSignals signals,
    IHubContext<IncidentHub> hub,
    LiveSnapshots snapshots,
    NwsClient nws,
    CotSender cot,
    CotSettings cotSettings,
    LiveOptions options,
    BrokerConfig broker,
    TimeProvider clock,
    ILogger<LiveRunner> log) : BackgroundService
{
    private readonly Dictionary<Guid, IncidentRuntime> _incidents = new();
    private readonly Channel<(string Topic, byte[] Payload)> _inbox = Channel.CreateUnbounded<(string, byte[])>();
    private readonly Dictionary<Guid, DateTimeOffset> _nwsFetched = new();
    private DateTimeOffset _nextCot;
    private MqttConnection? _mqtt;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (broker.Settings is { } mqttSettings)
        {
            _mqtt = new MqttConnection(mqttSettings, "lookout-api", m => log.LogInformation("{Message}", m))
            {
                OnMessage = (topic, payload) => { _inbox.Writer.TryWrite((topic, payload)); return Task.CompletedTask; },
            };
            await _mqtt.StartAsync([Topics.AllUplinks, Topics.AllDroneFronts, Topics.AllWeather], ct);
        }
        else
        {
            log.LogWarning("No MQTT broker configured: alerts are marked sent without leaving the server (loopback mode).");
        }

        await LoadRunningIncidentsAsync(ct);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.TickSeconds));
        do
        {
            try
            {
                while (signals.Reader.TryRead(out var s)) await HandleSignalAsync(s, ct);
                while (_inbox.Reader.TryRead(out var m)) await HandleMessageAsync(m.Topic, m.Payload, ct);
                foreach (var rt in _incidents.Values.ToList()) await TickAsync(rt, ct);
                await ExportCotAsync(ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogError(e, "Live runner tick failed; continuing");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
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
        foreach (var id in running) await LoadAsync(id, ct);
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

        // Re-offer alerts that were still open when the server last stopped.
        var open = await db.Alerts.AsNoTracking()
            .Where(a => a.IncidentId == id && (a.State == Engine.Workflow.AlertState.Queued || a.State == Engine.Workflow.AlertState.Sent || a.State == Engine.Workflow.AlertState.Escalated))
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
            case IncidentCreated:
                await LoadAsync(s.IncidentId, ct);
                break;
            case LayoutChanged when _incidents.TryGetValue(s.IncidentId, out var rt):
                await using (var scope = scopes.CreateAsyncScope())
                    await ReloadLayoutAsync(rt, scope.ServiceProvider.GetRequiredService<LookoutDb>(), ct);
                break;
            case AlertClosed c when _incidents.TryGetValue(s.IncidentId, out var rt2):
                rt2.Governor.Clear(c.CrewId, c.AlertId);
                break;
        }
    }

    private async Task HandleMessageAsync(string topic, byte[] payload, CancellationToken ct)
    {
        if (Topics.Parse(topic) is not { } t) return;
        var rt = _incidents.GetValueOrDefault(t.Incident) ?? await LoadAsync(t.Incident, ct);
        if (rt is null) return;

        try
        {
            switch (t.Path)
            {
                case ["crew", var crewId, "up"]:
                    await HandleUplinkAsync(rt, crewId, payload, ct);
                    break;
                case ["drone", "front"]:
                    rt.UpdateFront(SurveyJson.Decode<DroneFrontMessage>(payload));
                    break;
                case ["wx"] when rt.Incident.WindSource == WindSource.Field:
                    var wx = SurveyJson.Decode<WeatherMessage>(payload);
                    rt.UpdateWind(new SpreadConditions(wx.WindFromDeg, wx.MidflameWindMs, wx.HeadRateMPerMin), wx.Source);
                    break;
                case ["wx"]:
                    // NWS mode: keep the field's head-fire rate, which NWS cannot provide.
                    var fieldWx = SurveyJson.Decode<WeatherMessage>(payload);
                    rt.UpdateWind(rt.Wind with { HeadRateMPerMin = fieldWx.HeadRateMPerMin }, rt.WindSource);
                    break;
            }
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            log.LogWarning("Dropped malformed message on {Topic}: {Error}", topic, e.Message);
        }
    }

    private async Task HandleUplinkAsync(IncidentRuntime rt, string crewId, byte[] payload, CancellationToken ct)
    {
        if (rt.Crews.All(c => c.CrewId != crewId)) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();

        switch (WireFormat.DecodeUplink(payload))
        {
            case PositionReport p:
                var scenarioMin = rt.ScenarioMinute(p.Time);
                var local = rt.Frame.ToLocal(p.Lon, p.Lat);
                if (!rt.Fixes.TryGetValue(crewId, out var prev) || scenarioMin > prev.ScenarioMin)
                    rt.Fixes[crewId] = (scenarioMin, local, p.BatteryPercent);
                db.CrewFixes.Add(new CrewFix
                {
                    Time = p.Time, IncidentId = rt.Incident.Id, CrewId = crewId,
                    Position = new NetTopologySuite.Geometries.Point(p.Lon, p.Lat) { SRID = 4326 },
                    ScenarioMin = scenarioMin, Bytes = payload.Length,
                });
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { /* duplicate delivery of the same fix (QoS 1): already stored */ }
                break;

            case AckReport a:
                var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
                var acked = await alerts.AcknowledgeFromDeviceAsync(rt.Incident.Id, crewId, a.AlertSequence, ct);
                if (acked is not null)
                {
                    rt.Governor.Clear(crewId, acked.Id);
                    await hub.Clients.Group(rt.Incident.Id.ToString()).SendAsync("alert", AlertDto.From(acked), ct);
                }
                break;
        }
    }

    private async Task TickAsync(IncidentRuntime rt, CancellationToken ct)
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

        foreach (var crew in rt.Crews)
        {
            if (!rt.Profiles.TryGetValue(crew.CrewId, out var profile) || !rt.Zones.TryGetValue(profile.Route.SafetyZoneId, out var zone)) continue;
            var hasFix = rt.Fixes.TryGetValue(crew.CrewId, out var fix);
            // Give devices a few minutes to report in before treating silence as a comms problem.
            if (!hasFix && t < rt.Scenario.Alerts.CommsStaleMin) continue;
            var known = hasFix ? fix.Position : profile.Samples[0].Point;

            var a = rt.Monitors[crew.CrewId].Assess(profile, zone, known, hasFix ? fix.ScenarioMin : null, rt.Front, rt.Wind, t, rt.ClockLabel);
            rt.LastAssessment[crew.CrewId] = a;
            if (a.Change is not { } change) continue;

            var alert = await alerts.RaiseAsync(rt.Incident.Id, crew.CrewId, change.To, change.Text, change.Bytes, t, ct);
            Offer(rt, alert, a.Cutoff.LeaveByMin);
            await group.SendAsync("alert", AlertDto.From(alert), ct);
        }

        var timeout = TimeSpan.FromMinutes(options.AckTimeoutMin / rt.Incident.ClockRate);
        foreach (var escalated in await alerts.EscalateOverdueAsync(rt.Incident.Id, timeout, ct))
            await group.SendAsync("alert", AlertDto.From(escalated), ct);

        foreach (var tx in rt.Governor.Due(t * 60))
        {
            if (_mqtt is { IsConnected: true }) await _mqtt.PublishAsync(Topics.Downlink(rt.Incident.Id, tx.CrewId), tx.Payload, ct);
            else if (_mqtt is not null) continue; // broker down: leave it for the next tick
            await alerts.MarkSentAsync(tx.AlertId, ct);
            var sent = await scope.ServiceProvider.GetRequiredService<LookoutDb>().Alerts.AsNoTracking().SingleAsync(x => x.Id == tx.AlertId, ct);
            await group.SendAsync("alert", AlertDto.From(sent), ct);
        }

        var frame = LiveFrame.Build(rt, t, now);
        snapshots.Set(rt.Incident.Id, frame);
        await group.SendAsync("frame", frame, ct);
    }

    /// <summary>Crew positions, fire perimeter and cut-off warnings as CoT, for TAK clients.</summary>
    private async Task ExportCotAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (now < _nextCot) return;
        _nextCot = now.AddSeconds(cotSettings.IntervalSec);
        var all = new List<XElement>();
        foreach (var rt in _incidents.Values)
        {
            var events = new List<XElement>();
            var id = rt.Incident.Id;
            for (var i = 0; i < rt.Front.NumGeometries; i++)
            {
                if (rt.Front.GetGeometryN(i) is not NetTopologySuite.Geometries.Polygon p || p.IsEmpty) continue;
                var ring = p.ExteriorRing.Coordinates.Select(c => { var (lon, lat) = rt.Frame.ToLonLat(c); return new[] { lon, lat }; }).ToList();
                events.Add(CotBuilder.FirePerimeter(id, ring, now));
            }
            foreach (var crew in rt.Crews)
            {
                if (!rt.Fixes.TryGetValue(crew.CrewId, out var fix)) continue;
                var (lon, lat) = rt.Frame.ToLonLat(fix.Position);
                var level = rt.Monitors.TryGetValue(crew.CrewId, out var m) ? m.Reported : AlertLevel.None;
                rt.LastAssessment.TryGetValue(crew.CrewId, out var a);
                var summary = a?.Findings.OrderByDescending(f => f.Level).FirstOrDefault()?.Summary;
                events.Add(CotBuilder.Crew(id, crew.CrewId, lat, lon, level.ToString().ToLowerInvariant(), summary, fix.Battery, now));
                if (level >= AlertLevel.Warning && a?.Cutoff.CutPoint is { } cut && summary is not null)
                {
                    var (cutLon, cutLat) = rt.Frame.ToLonLat(cut);
                    events.Add(CotBuilder.CutoffWarning(id, crew.CrewId, cutLat, cutLon, level.ToString().ToLowerInvariant(), summary, now));
                }
            }
            snapshots.SetCot(id, events);
            all.AddRange(events);
        }
        if (all.Count > 0) await cot.SendAsync(all, ct);
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

    private async Task RefreshNwsAsync(IncidentRuntime rt, DateTimeOffset now, CancellationToken ct)
    {
        if (_nwsFetched.TryGetValue(rt.Incident.Id, out var last) && now - last < TimeSpan.FromMinutes(10)) return;
        _nwsFetched[rt.Incident.Id] = now;
        try
        {
            var w = await nws.GetAsync(rt.Frame.OriginLat, rt.Frame.OriginLon, ct);
            if (w.WindKmh is { } kmh)
                rt.UpdateWind(rt.Wind with { WindFromDeg = w.WindFromDeg ?? rt.Wind.WindFromDeg, MidflameWindMs = NwsClient.MidflameMs(kmh) },
                    $"NWS {w.Station} {w.ObservedAt:HH:mm}Z");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            log.LogWarning("NWS fetch failed for incident {Id}: {Error}; keeping last wind", rt.Incident.Id, e.Message);
        }
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
