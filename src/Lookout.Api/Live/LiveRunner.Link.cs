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

/// <summary>The crew link: the device directory, and messages arriving from devices, drones and weather stations.</summary>
public sealed partial class LiveRunner
{
    /// <summary>Device id to the incident and crew it reports for, from the devices table. Replaced whole, never mutated, so the MQTT thread can read it.</summary>
    private volatile IReadOnlyDictionary<string, (Guid Incident, string Crew)> _devices = new Dictionary<string, (Guid, string)>();
    private volatile bool _devicesLoaded;
    private DateTimeOffset _nextDeviceReload;
    /// <summary>A safety net: the directory also reloads on every assignment, but a missed signal must not last.</summary>
    private static readonly TimeSpan DeviceReloadInterval = TimeSpan.FromSeconds(60);
    private readonly HashSet<string> _unknownDevicesLogged = [];

    private async Task ReloadDevicesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();
        _devices = (await db.Devices.AsNoTracking().ToListAsync(ct))
            .ToDictionary(d => d.DeviceId, d => (d.IncidentId, d.CrewId));
        _devicesLoaded = health.DevicesLoaded = true;
        _nextDeviceReload = clock.GetUtcNow() + DeviceReloadInterval;
        _unknownDevicesLogged.Clear();
    }

    private IEnumerable<string> DevicesOf(Guid incident, string crewId) =>
        _devices.Where(d => d.Value.Incident == incident && d.Value.Crew == crewId).Select(d => d.Key);

    private async Task HandleMessageAsync(string topic, byte[] payload, IReadOnlyDictionary<string, (Guid Incident, string Crew)>? devicesAtArrival, CancellationToken ct)
    {
        // Crew devices: the registry, not the topic, says which incident and crew a device reports for.
        if (Topics.ParseDevice(topic) is { Up: true } dev)
        {
            activity.Heard(dev.DeviceId, BatteryOf(payload));
            // Unknown at arrival but assigned since: it belonged to no crew then, so the current owner is safe.
            if (!(devicesAtArrival ?? _devices).TryGetValue(dev.DeviceId, out var owner) && !_devices.TryGetValue(dev.DeviceId, out owner))
            {
                if (_unknownDevicesLogged.Add(dev.DeviceId))
                    log.LogWarning("Uplink from unregistered device {Device} dropped; assign it to a crew first", dev.DeviceId);
                return;
            }
            activity.MarkAssigned(dev.DeviceId);
            var crewRt = _incidents.GetValueOrDefault(owner.Incident) ?? await LoadAsync(owner.Incident, ct);
            if (crewRt is not null) await HandleUplinkAsync(crewRt, owner.Crew, payload, ct, dev.DeviceId);
            else if (_unknownDevicesLogged.Add(dev.DeviceId))
                log.LogWarning("Uplink from device {Device} dropped: it is still assigned to incident {Incident}, which is not running; assign it to a crew in a running incident",
                    dev.DeviceId, owner.Incident);
            return;
        }

        if (Topics.Parse(topic) is not { } t) return;
        var rt = _incidents.GetValueOrDefault(t.Incident) ?? await LoadAsync(t.Incident, ct);
        if (rt is null) return;

        switch (t.Path)
        {
            case ["drone", "front"]:
                rt.UpdateFront(SurveyJson.Decode<DroneFrontMessage>(payload));
                break;
            case ["wx"]:
                var wx = SurveyJson.Decode<WeatherMessage>(payload);
                if (!double.IsFinite(wx.WindFromDeg) || !double.IsFinite(wx.MidflameWindMs) || wx.MidflameWindMs is < 0 or > 60
                    || !double.IsFinite(wx.HeadRateMPerMin) || wx.HeadRateMPerMin is <= 0 or > 1000)
                    throw new FormatException($"Weather values out of range: {wx}");
                if (rt.Incident.WindSource == WindSource.Field)
                    rt.UpdateWind(new SpreadConditions(((wx.WindFromDeg % 360) + 360) % 360, wx.MidflameWindMs, wx.HeadRateMPerMin), wx.Source);
                else // NWS mode: keep NWS wind, take only the head-fire rate NWS cannot provide.
                    rt.UpdateWind(rt.Wind with { HeadRateMPerMin = wx.HeadRateMPerMin }, rt.WindSource);
                break;
        }
    }

    /// <summary>Battery from a position report; null for an ack or a malformed message (handled, and logged, later).</summary>
    private static int? BatteryOf(byte[] payload)
    {
        try { return WireFormat.DecodeUplink(payload) is PositionReport p ? p.BatteryPercent : null; }
        catch (FormatException) { return null; }
    }

    private async Task HandleUplinkAsync(IncidentRuntime rt, string crewId, byte[] payload, CancellationToken ct, string? deviceId = null)
    {
        if (rt.Crews.All(c => c.CrewId != crewId)) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LookoutDb>();

        var uplink = WireFormat.DecodeUplink(payload);
        Telemetry.UplinkMessages.Add(1, new KeyValuePair<string, object?>("kind", uplink is PositionReport ? "position" : "ack"));
        switch (uplink)
        {
            case PositionReport p:
                // Trust the device clock only up to the server's: one fix stamped in the future would
                // otherwise make every later fix look older and freeze the crew's position.
                var received = clock.GetUtcNow();
                var fixTime = p.Time > received.AddSeconds(5) ? received : p.Time;
                if (fixTime != p.Time) log.LogWarning("Crew {Crew} fix stamped {Stamp:o} is in the future; using receive time", crewId, p.Time);
                var scenarioMin = rt.ScenarioMinute(fixTime);
                var local = rt.Frame.ToLocal(p.Lon, p.Lat);
                if (!rt.Fixes.TryGetValue(crewId, out var prev) || scenarioMin > prev.ScenarioMin)
                    rt.Fixes[crewId] = (scenarioMin, local, p.BatteryPercent);
                db.CrewFixes.Add(new CrewFix
                {
                    Time = fixTime, IncidentId = rt.Incident.Id, CrewId = crewId,
                    Position = new NetTopologySuite.Geometries.Point(p.Lon, p.Lat) { SRID = 4326 },
                    ScenarioMin = scenarioMin, Bytes = payload.Length,
                });
                try { await db.SaveChangesAsync(ct); }
                catch (DbUpdateException) { /* duplicate delivery of the same fix (QoS 1): already stored */ }
                break;

            case AckReport a:
                var alerts = scope.ServiceProvider.GetRequiredService<AlertService>();
                var acked = await alerts.AcknowledgeFromDeviceAsync(rt.Incident.Id, crewId, a.AlertSequence, ct, deviceId);
                if (acked is not null)
                {
                    rt.Governor.Clear(crewId, acked.Id);
                    await hub.Clients.Group(rt.Incident.Id.ToString()).SendAsync("alert", AlertDto.From(acked), ct);
                }
                break;
        }
    }
}
