using System.Threading.Channels;

namespace Lookout.Api.Live;

/// <summary>Things the HTTP side tells the live runner: reload routes, stop sending an alert, pick up a new incident.</summary>
public abstract record IncidentSignal(Guid IncidentId);
public sealed record IncidentCreated(Guid IncidentId) : IncidentSignal(IncidentId);
public sealed record LayoutChanged(Guid IncidentId) : IncidentSignal(IncidentId);
public sealed record AlertClosed(Guid IncidentId, string CrewId, Guid AlertId) : IncidentSignal(IncidentId);
/// <summary>A crew uplink that arrived over HTTPS (a phone) instead of MQTT; same bytes, same handling.</summary>
public sealed record UplinkReceived(Guid IncidentId, string CrewId, byte[] Payload) : IncidentSignal(IncidentId);

/// <summary>In-process queue from request handlers to the live runner's single processing loop.</summary>
public sealed class IncidentSignals
{
    private readonly Channel<IncidentSignal> _channel = Channel.CreateUnbounded<IncidentSignal>();
    public void Post(IncidentSignal s) => _channel.Writer.TryWrite(s);
    public ChannelReader<IncidentSignal> Reader => _channel.Reader;
}
