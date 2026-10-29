using System.Globalization;
using System.Text;
using Lookout.Api.Data;
using Lookout.Engine.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Lookout.Api.Live;

/// <summary>After-action numbers for an incident: how fast warnings were acknowledged, and what the link carried.</summary>
public static class AfterAction
{
    public static async Task<object> SummaryAsync(LookoutDb db, Guid incidentId, CancellationToken ct)
    {
        var incident = await db.Incidents.AsNoTracking().SingleOrDefaultAsync(i => i.Id == incidentId, ct)
            ?? throw new NotFoundException($"Incident {incidentId}");
        var alerts = await db.Alerts.AsNoTracking().Where(a => a.IncidentId == incidentId).ToListAsync(ct);
        var fixes = await db.CrewFixes.AsNoTracking().Where(f => f.IncidentId == incidentId)
            .Select(f => new { f.CrewId, f.ScenarioMin, f.Bytes }).ToListAsync(ct);
        var crews = await db.Crews.AsNoTracking().Where(c => c.IncidentId == incidentId).OrderBy(c => c.CrewId).ToListAsync(ct);
        // Who acknowledged comes from the audit trail's recorded role and actor, not from names:
        // "ic" is the incident commander; "device-*" acks arrived over the satellite uplink; any
        // other crew actor is a phone acknowledging over HTTPS.
        var ackEvents = await db.AuditEvents.AsNoTracking()
            .Where(e => e.IncidentId == incidentId && e.Type == "alert.acknowledged")
            .Select(e => new { e.AlertId, e.Role, e.Actor }).ToListAsync(ct);
        var icAcked = ackEvents.Where(e => e.Role == "ic").Select(e => e.AlertId).ToHashSet();
        var deviceAcks = ackEvents.Count(e => e.Actor.StartsWith("device-", StringComparison.Ordinal));

        double ToScenario(TimeSpan real) => real.TotalMinutes * incident.ClockRate;

        return new
        {
            incident.Id,
            incident.Name,
            Status = incident.Status.ToString().ToLowerInvariant(),
            incident.ClockRate,
            Crews = crews.Select(c =>
            {
                var mine = alerts.Where(a => a.CrewId == c.CrewId).ToList();
                var needAck = mine.Where(a => a.RequiresAck && a.SentAt is not null).ToList();
                var acked = needAck.Where(a => a.AcknowledgedAt is not null).ToList();
                var ackMinutes = acked.Select(a => ToScenario(a.AcknowledgedAt!.Value - a.SentAt!.Value)).Order().ToList();
                var times = fixes.Where(f => f.CrewId == c.CrewId).Select(f => f.ScenarioMin).Order().ToList();
                var longestGap = times.Count < 2 ? (double?)null : times.Zip(times.Skip(1), (a, b) => b - a).Max();
                return new
                {
                    c.CrewId,
                    c.Name,
                    AlertsByLevel = mine.GroupBy(a => a.Level.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count()),
                    WarningsSent = needAck.Count,
                    // Raised but never delivered on the link (no device, broker down); acked or escalated by other means if at all.
                    WarningsNotDelivered = mine.Count(a => a.RequiresAck && a.SentAt is null && a.State is not (AlertState.Superseded or AlertState.Cancelled)),
                    Acknowledged = acked.Count,
                    AcknowledgedByIc = acked.Count(a => icAcked.Contains(a.Id)),
                    Escalated = mine.Count(a => a.EscalatedAt is not null),
                    MedianAckScenarioMin = ackMinutes.Count == 0 ? (double?)null : Math.Round(ackMinutes[ackMinutes.Count / 2], 2),
                    MaxAckScenarioMin = ackMinutes.Count == 0 ? (double?)null : Math.Round(ackMinutes[^1], 2),
                    FixesReceived = times.Count,
                    LongestFixGapScenarioMin = longestGap is null ? (double?)null : Math.Round(longestGap.Value, 1),
                };
            }),
            Link = new
            {
                DownlinkMessages = alerts.Sum(a => a.SendCount),
                DownlinkBytes = alerts.Sum(a => (long)(a.Bytes + Link.WireFormat.AlertHeaderBytes) * a.SendCount),
                UplinkPositionReports = fixes.Count,
                UplinkAcks = deviceAcks,
                UplinkBytes = fixes.Sum(f => (long)f.Bytes) + deviceAcks * (long)Link.WireFormat.AckBytes,
                LargestDownlinkBytes = alerts.Count == 0 ? 0 : alerts.Max(a => a.Bytes + Link.WireFormat.AlertHeaderBytes),
            },
            OpenAlerts = alerts.Count(a => a.State is AlertState.Queued or AlertState.Escalated || (a.State == AlertState.Sent && a.RequiresAck)),
        };
    }

    /// <summary>The audit trail as CSV (RFC 4180), oldest first, for an after-action review or a records request.</summary>
    public static async Task<string> AuditCsvAsync(LookoutDb db, Guid incidentId, CancellationToken ct)
    {
        var rows = await db.AuditEvents.AsNoTracking().Where(e => e.IncidentId == incidentId)
            .OrderBy(e => e.Time).ThenBy(e => e.Id).ToListAsync(ct);
        var sb = new StringBuilder("time_utc,type,crew,alert_id,actor,role,details\r\n");
        foreach (var e in rows)
        {
            sb.Append(string.Join(',',
                e.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                Csv(e.Type), Csv(e.CrewId), e.AlertId?.ToString() ?? "", Csv(e.Actor), Csv(e.Role),
                Csv(e.Details?.RootElement.GetRawText())));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Csv(string? v) =>
        v is null ? "" : v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
}
