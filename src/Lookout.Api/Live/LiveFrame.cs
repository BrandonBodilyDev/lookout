using System.Collections.Concurrent;
using Lookout.Engine.Rules;

namespace Lookout.Api.Live;

/// <summary>What the dashboard draws for a live incident at one moment.</summary>
public static class LiveFrame
{
    public static object Build(IncidentRuntime rt, double t, DateTimeOffset now)
    {
        var f = rt.Frame;
        return new
        {
            IncidentId = rt.Incident.Id,
            T = Math.Round(t, 2),
            Clock = rt.ClockLabel(t),
            Wind = new { rt.Wind.WindFromDeg, rt.Wind.MidflameWindMs, rt.Wind.HeadRateMPerMin, Source = rt.WindSource },
            Fire = GeoDto.MultiPolygon(f, rt.Front),
            FrontAgeSec = rt.FrontTime is { } ft ? Math.Round((now - ft).TotalSeconds) : (double?)null,
            Crews = rt.Crews.Select(c =>
            {
                rt.Fixes.TryGetValue(c.CrewId, out var fix);
                var hasFix = rt.Fixes.ContainsKey(c.CrewId);
                rt.LastAssessment.TryGetValue(c.CrewId, out var a);
                var level = rt.Monitors.TryGetValue(c.CrewId, out var m) ? m.Reported : AlertLevel.None;
                var cut = a?.Cutoff;
                return new
                {
                    Id = c.CrewId,
                    Known = hasFix ? GeoDto.Point(f, fix.Position) : null,
                    FixAgeMin = hasFix ? Math.Round(t - fix.ScenarioMin, 1) : (double?)null,
                    Battery = hasFix ? fix.Battery : (int?)null,
                    EnRoute = a?.EnRoute ?? false,
                    Level = level.ToString().ToLowerInvariant(),
                    RawLevel = (a?.RawLevel ?? AlertLevel.None).ToString().ToLowerInvariant(),
                    InSafetyZone = cut?.InSafetyZone ?? false,
                    MarginMin = cut is null ? null : GeoDto.Finite(cut.MarginMin),
                    SlackMin = cut is null ? null : GeoDto.Finite(cut.SlackMin),
                    LeaveBy = cut is not null && double.IsFinite(cut.LeaveByMin) ? rt.ClockLabel(cut.LeaveByMin) : null,
                    FireAtCut = cut is not null && double.IsFinite(cut.FireEtaAtCutMin) ? rt.ClockLabel(cut.FireEtaAtCutMin) : null,
                    Cut = cut?.CutPoint is { } cp ? GeoDto.Point(f, cp) : null,
                    LeaveNowZone = cut?.TriggerZone is { IsEmpty: false } tz ? GeoDto.MultiPolygon(f, tz) : null,
                    Findings = (a?.Findings ?? []).Select(x => new { x.Rule, Level = x.Level.ToString().ToLowerInvariant(), x.Summary }),
                };
            }),
        };
    }
}

/// <summary>Latest frame per incident, for clients that join mid-incident.</summary>
public sealed class LiveSnapshots
{
    private readonly ConcurrentDictionary<Guid, object> _frames = new();
    public void Set(Guid incidentId, object frame) => _frames[incidentId] = frame;
    public object? Get(Guid incidentId) => _frames.GetValueOrDefault(incidentId);
}
