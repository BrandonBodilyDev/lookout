namespace Lookout.Link;

/// <param name="MinSpacingSec">Minimum time between transmissions to one crew device. Iridium SBD needs about 40 s between sessions.</param>
/// <param name="RepeatSec">An alert that needs acknowledgement is sent again this often until it is acknowledged.</param>
public sealed record GovernorSettings(double MinSpacingSec = 40, double RepeatSec = 60);

/// <summary>
/// Decides what goes out on each crew's downlink, and when. One outbound slot per crew: a newer
/// alert replaces an older one that has not been acknowledged (the engine supersedes it), so the
/// link never spends a session on stale news. Transmissions to one device are spaced by the modem's
/// session interval; when several crews are due, the most severe goes first. Unacknowledged
/// warnings repeat until acknowledged; informational messages go once.
///
/// Time is passed in by the caller in seconds on the scenario clock, so an accelerated demo keeps
/// the same link behaviour per scenario minute as a real-time incident.
/// </summary>
public sealed class LinkGovernor(GovernorSettings settings)
{
    private readonly Dictionary<string, Slot> _slots = new();
    private readonly Dictionary<string, double> _lastTx = new();

    private sealed class Slot
    {
        public required Guid AlertId { get; init; }
        public required int Level { get; init; }
        public required byte[] Payload { get; init; }
        public required bool RequiresAck { get; init; }
        public double? LastSentSec { get; set; }
    }

    public GovernorSettings Settings => settings;

    /// <summary>Put an alert in the crew's slot, replacing whatever was there.</summary>
    public void Offer(string crewId, Guid alertId, int level, byte[] payload, bool requiresAck)
    {
        if (payload.Length > WireFormat.MaxDownlinkBytes)
            throw new ArgumentException($"Downlink payload is {payload.Length} bytes; the limit is {WireFormat.MaxDownlinkBytes}.", nameof(payload));
        _slots[crewId] = new Slot { AlertId = alertId, Level = level, Payload = payload, RequiresAck = requiresAck };
    }

    /// <summary>The crew acknowledged (or the alert was cancelled): stop repeating it.</summary>
    public void Clear(string crewId, Guid alertId)
    {
        if (_slots.TryGetValue(crewId, out var s) && s.AlertId == alertId) _slots.Remove(crewId);
    }

    public bool HasPending(string crewId) => _slots.ContainsKey(crewId);

    /// <summary>
    /// Transmissions due at <paramref name="nowSec"/>, most severe first. The caller reports each one
    /// back with <see cref="Delivered"/> or <see cref="Failed"/>.
    /// </summary>
    public IReadOnlyList<Transmission> Due(double nowSec)
    {
        var due = new List<Transmission>();
        foreach (var (crew, slot) in _slots.OrderByDescending(kv => kv.Value.Level))
        {
            var hadTx = _lastTx.TryGetValue(crew, out var last);
            var spacingOk = !hadTx || nowSec - last >= settings.MinSpacingSec;
            var firstSend = slot.LastSentSec is null;
            var repeatDue = slot.RequiresAck && slot.LastSentSec is { } ls && nowSec - ls >= settings.RepeatSec;
            if (!spacingOk || !(firstSend || repeatDue)) continue;

            due.Add(new Transmission(crew, slot.AlertId, slot.Payload, IsRepeat: !firstSend, slot.LastSentSec, hadTx ? last : null));
            slot.LastSentSec = nowSec;
            _lastTx[crew] = nowSec;
        }
        return due;
    }

    /// <summary>The message left on the link. Informational messages go once, so their slot is freed.</summary>
    public void Delivered(Transmission t)
    {
        if (_slots.TryGetValue(t.CrewId, out var s) && s.AlertId == t.AlertId && !s.RequiresAck) _slots.Remove(t.CrewId);
    }

    /// <summary>The message could not be handed to the link (broker down): undo, so it is due again next time.</summary>
    public void Failed(Transmission t)
    {
        if (_slots.TryGetValue(t.CrewId, out var s) && s.AlertId == t.AlertId) s.LastSentSec = t.PreviousSentSec;
        if (t.PreviousTxSec is { } p) _lastTx[t.CrewId] = p; else _lastTx.Remove(t.CrewId);
    }
}

/// <param name="PreviousSentSec">For rollback: when this alert was last sent before now.</param>
/// <param name="PreviousTxSec">For rollback: when this device last transmitted before now.</param>
public sealed record Transmission(string CrewId, Guid AlertId, byte[] Payload, bool IsRepeat, double? PreviousSentSec = null, double? PreviousTxSec = null);
