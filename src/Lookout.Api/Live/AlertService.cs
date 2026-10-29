using System.Text.Json;
using Lookout.Api.Auth;
using Lookout.Api.Data;
using Lookout.Engine.Rules;
using Lookout.Engine.Workflow;
using Microsoft.EntityFrameworkCore;

namespace Lookout.Api.Live;

/// <summary>
/// Every change to an alert goes through here: the state machine decides if it is allowed, the
/// alert row and its audit event are saved in one transaction, and the alert's row version makes
/// concurrent edits fail loudly instead of overwriting each other.
/// </summary>
public sealed class AlertService(LookoutDb db, TimeProvider clock)
{
    /// <summary>Warnings and critical alerts must be acknowledged; watch and all-clear messages are informational.</summary>
    public static bool NeedsAck(AlertLevel level) => level >= AlertLevel.Warning;

    /// <summary>Queue a new alert for a crew, superseding any of its alerts still open.</summary>
    public async Task<AlertRecord> RaiseAsync(Guid incidentId, string crewId, AlertLevel level, string text, int bytes, double scenarioMin, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var alert = new AlertRecord
        {
            Id = Guid.CreateVersion7(),
            IncidentId = incidentId,
            CrewId = crewId,
            Level = level,
            RequiresAck = NeedsAck(level),
            Text = text,
            Bytes = bytes,
            ScenarioMin = scenarioMin,
            RaisedAt = now,
        };

        var open = await db.Alerts
            .Where(a => a.IncidentId == incidentId && a.CrewId == crewId
                && (a.State == AlertState.Queued || a.State == AlertState.Sent || a.State == AlertState.Escalated))
            .ToListAsync(ct);
        foreach (var old in open)
        {
            Transition(old, AlertAction.Supersede, Actor.Engine, now, new { supersededBy = alert.Id });
            old.SupersededBy = alert.Id;
        }

        db.Alerts.Add(alert);
        Audit(alert, "alert.raised", Actor.Engine, now, new { level = level.ToString(), text, bytes, scenarioMin });
        await db.SaveChangesAsync(ct);
        return alert;
    }

    /// <summary>The link governor published the alert on the crew's downlink.</summary>
    public async Task MarkSentAsync(Guid alertId, CancellationToken ct = default)
    {
        var alert = await db.Alerts.SingleAsync(a => a.Id == alertId, ct);
        var now = clock.GetUtcNow();
        alert.SendCount++;
        if (alert.State == AlertState.Queued)
        {
            alert.SentAt = now;
            Transition(alert, AlertAction.Send, Actor.Link, now, new { sendCount = alert.SendCount });
        }
        else
        {
            Audit(alert, "alert.resent", Actor.Link, now, new { sendCount = alert.SendCount });
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<AlertRecord> AcknowledgeAsync(Guid incidentId, Guid alertId, uint expectedVersion, Actor actor, CancellationToken ct = default)
    {
        var alert = await Load(incidentId, alertId, ct);
        if (actor.Role == Roles.Crew && actor.CrewId != alert.CrewId)
            throw new ForbiddenActionException($"Crew {actor.CrewId} cannot acknowledge an alert for crew {alert.CrewId}.");
        CheckVersion(alert, expectedVersion);

        var now = clock.GetUtcNow();
        Transition(alert, AlertAction.Acknowledge, actor, now, new { onBehalf = actor.Role == Roles.IncidentCommander });
        alert.AcknowledgedAt = now;
        alert.AcknowledgedBy = actor.Name;
        await Save(alert, expectedVersion, ct);
        return alert;
    }

    public async Task<AlertRecord> CancelAsync(Guid incidentId, Guid alertId, uint expectedVersion, string reason, Actor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ConflictException("A cancellation needs a reason.");
        var alert = await Load(incidentId, alertId, ct);
        CheckVersion(alert, expectedVersion);
        var now = clock.GetUtcNow();
        Transition(alert, AlertAction.Cancel, actor, now, new { reason });
        alert.CancelReason = reason;
        await Save(alert, expectedVersion, ct);
        return alert;
    }

    /// <summary>Escalate sent alerts that needed an acknowledgement and did not get one in time.</summary>
    public async Task<IReadOnlyList<AlertRecord>> EscalateOverdueAsync(Guid incidentId, TimeSpan timeout, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - timeout;
        var overdue = await db.Alerts
            .Where(a => a.IncidentId == incidentId && a.State == AlertState.Sent && a.RequiresAck && a.SentAt < cutoff)
            .ToListAsync(ct);
        foreach (var a in overdue)
        {
            Transition(a, AlertAction.Escalate, Actor.Engine, now, new { waitedSeconds = (now - a.SentAt!.Value).TotalSeconds });
            a.EscalatedAt = now;
        }
        if (overdue.Count > 0) await db.SaveChangesAsync(ct);
        return overdue;
    }

    private async Task<AlertRecord> Load(Guid incidentId, Guid alertId, CancellationToken ct) =>
        await db.Alerts.SingleOrDefaultAsync(a => a.Id == alertId && a.IncidentId == incidentId, ct)
        ?? throw new NotFoundException($"Alert {alertId}");

    private static void CheckVersion(AlertRecord alert, uint expected)
    {
        if (alert.Version != expected)
            throw new ConflictException($"Alert changed since you loaded it (version {expected}, now {alert.Version}).") { Current = AlertDto.From(alert) };
    }

    /// <summary>Save with the caller's version as the concurrency check, so a race between load and save is also caught.</summary>
    private async Task Save(AlertRecord alert, uint expectedVersion, CancellationToken ct)
    {
        db.Entry(alert).Property(a => a.Version).OriginalValue = expectedVersion;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == alert.Id, ct);
            throw new ConflictException("Alert was changed by someone else at the same time.") { Current = AlertDto.From(current) };
        }
    }

    private void Transition(AlertRecord alert, AlertAction action, Actor actor, DateTimeOffset now, object details)
    {
        var from = alert.State;
        if (!AlertStateMachine.TryApply(from, action, out var to))
            throw new ConflictException($"Cannot {action.ToString().ToLowerInvariant()} an alert that is {from.ToString().ToLowerInvariant()}.") { Current = AlertDto.From(alert) };
        alert.State = to;
        Audit(alert, $"alert.{to.ToString().ToLowerInvariant()}", actor, now, new { from = from.ToString(), to = to.ToString(), details });
    }

    private void Audit(AlertRecord alert, string type, Actor actor, DateTimeOffset now, object details) =>
        db.AuditEvents.Add(new AuditEvent
        {
            Time = now,
            IncidentId = alert.IncidentId,
            AlertId = alert.Id,
            CrewId = alert.CrewId,
            Type = type,
            Actor = actor.Name,
            Role = actor.Role,
            Details = JsonSerializer.SerializeToDocument(details),
        });
}

public sealed record AlertDto(
    Guid Id, string CrewId, string Level, string State, bool RequiresAck, string Text, int Bytes, double ScenarioMin,
    DateTimeOffset RaisedAt, DateTimeOffset? SentAt, int SendCount, DateTimeOffset? EscalatedAt,
    DateTimeOffset? AcknowledgedAt, string? AcknowledgedBy, string? CancelReason, uint Version)
{
    public static AlertDto From(AlertRecord a) => new(
        a.Id, a.CrewId, a.Level.ToString().ToLowerInvariant(), a.State.ToString().ToLowerInvariant(), a.RequiresAck, a.Text, a.Bytes,
        a.ScenarioMin, a.RaisedAt, a.SentAt, a.SendCount, a.EscalatedAt, a.AcknowledgedAt, a.AcknowledgedBy, a.CancelReason, a.Version);
}
