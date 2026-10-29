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

/// <summary>Outbound integrations other than the crew link: TAK (Cursor on Target) and NWS weather.</summary>
public sealed partial class LiveRunner
{
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
                events.Add(CotBuilder.Crew(id, crew.CrewId, lat, lon, level.ToString().ToLowerInvariant(), summary, fix.Battery >= 0 ? fix.Battery : null, now));
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

    /// <summary>
    /// Starts an NWS fetch off the loop (it can take tens of seconds when NWS is slow); the result
    /// comes back as a signal and is applied on the loop.
    /// </summary>
    private Task RefreshNwsAsync(IncidentRuntime rt, DateTimeOffset now, CancellationToken ct)
    {
        if (_nwsFetched.TryGetValue(rt.Incident.Id, out var last) && now - last < TimeSpan.FromMinutes(10)) return Task.CompletedTask;
        _nwsFetched[rt.Incident.Id] = now;
        var (id, lat, lon) = (rt.Incident.Id, rt.Frame.OriginLat, rt.Frame.OriginLon);
        _ = Task.Run(async () =>
        {
            try
            {
                var w = await nws.GetAsync(lat, lon, ct);
                if (w.WindKmh is { } kmh)
                    signals.Post(new NwsWindReceived(id, w.WindFromDeg, NwsClient.MidflameMs(kmh), $"NWS {w.Station} {w.ObservedAt:HH:mm}Z"));
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log.LogWarning("NWS fetch failed for incident {Id}: {Error}; keeping last wind", id, e.Message);
            }
        }, ct);
        return Task.CompletedTask;
    }
}
