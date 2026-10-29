using System.Text.Json;
using Lookout.Engine.Rules;
using Lookout.Engine.Workflow;
using NetTopologySuite.Geometries;

namespace Lookout.Api.Data;

/// <summary>A live run of a scenario. Routes, zones and crews are copied in so the IC can edit them.</summary>
public sealed class Incident
{
    public Guid Id { get; set; }
    public required string ScenarioId { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Wall-clock time that maps to scenario minute 0.</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>Scenario minutes per real minute (1 = real time; the demo runs faster).</summary>
    public double ClockRate { get; set; } = 1;
    public IncidentStatus Status { get; set; } = IncidentStatus.Running;
    /// <summary>Where the engine's wind comes from: the field (scenario/drone station) or the National Weather Service.</summary>
    public WindSource WindSource { get; set; } = WindSource.Field;
    public uint Version { get; set; }
}

public enum IncidentStatus { Running, Closed }

public enum WindSource { Field, Nws }

public sealed class CrewRecord
{
    public Guid IncidentId { get; set; }
    public required string CrewId { get; set; }
    public required string Name { get; set; }
    /// <summary>Primary escape route.</summary>
    public required string RouteId { get; set; }
    /// <summary>Other escape routes, in order of preference.</summary>
    public string[] AlternateRouteIds { get; set; } = [];
    public int Size { get; set; }
    public required string Pace { get; set; }
}

public sealed class SafetyZoneRecord
{
    public Guid IncidentId { get; set; }
    public required string ZoneId { get; set; }
    public required string Name { get; set; }
    /// <summary>WGS84 polygon (SRID 4326).</summary>
    public required Polygon Area { get; set; }
    public uint Version { get; set; }
}

public sealed class EscapeRouteRecord
{
    public Guid IncidentId { get; set; }
    public required string RouteId { get; set; }
    public required string Name { get; set; }
    public required string SafetyZoneId { get; set; }
    /// <summary>WGS84 line with ground elevation in Z (SRID 4326).</summary>
    public required LineString Path { get; set; }
    public uint Version { get; set; }
}

/// <summary>One alert message to one crew, and where it is in its lifecycle.</summary>
public sealed class AlertRecord
{
    public Guid Id { get; set; }
    public Guid IncidentId { get; set; }
    public required string CrewId { get; set; }
    /// <summary>Per-crew sequence number carried on the link; a device acknowledges by this number.</summary>
    public int LinkSeq { get; set; }
    public AlertLevel Level { get; set; }
    public AlertState State { get; set; } = AlertState.Queued;
    public bool RequiresAck { get; set; }
    public required string Text { get; set; }
    public int Bytes { get; set; }
    public double ScenarioMin { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public int SendCount { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public Guid? SupersededBy { get; set; }
    public string? CancelReason { get; set; }
    /// <summary>Optimistic concurrency token, mapped to PostgreSQL's xmin system column.</summary>
    public uint Version { get; set; }
}

/// <summary>
/// Append-only audit trail (TimescaleDB hypertable on <see cref="Time"/>). A database trigger
/// rejects UPDATE and DELETE, so history cannot be rewritten through the application.
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset Time { get; set; }
    public Guid IncidentId { get; set; }
    public Guid? AlertId { get; set; }
    public string? CrewId { get; set; }
    public required string Type { get; set; }
    public required string Actor { get; set; }
    public required string Role { get; set; }
    public JsonDocument? Details { get; set; }
}

/// <summary>
/// A crew device on the link (a satellite messenger; in SBD terms, a modem IMEI) and the one
/// incident and crew it reports for. Reassigning a device moves it; it is never in two places.
/// </summary>
public sealed class DeviceRecord
{
    public required string DeviceId { get; set; }
    public Guid IncidentId { get; set; }
    public required string CrewId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public required string AssignedBy { get; set; }
}

/// <summary>A crew phone's Web Push subscription, valid for one incident and one crew.</summary>
public sealed class PushSubscriptionRecord
{
    public long Id { get; set; }
    public Guid IncidentId { get; set; }
    public required string CrewId { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Crew GPS fixes as received over the uplink (TimescaleDB hypertable).</summary>
public sealed class CrewFix
{
    public DateTimeOffset Time { get; set; }
    public Guid IncidentId { get; set; }
    public required string CrewId { get; set; }
    public required Point Position { get; set; }
    public double ScenarioMin { get; set; }
    public int Bytes { get; set; }
}
