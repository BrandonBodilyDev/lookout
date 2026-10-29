using System.Buffers;
using MQTTnet;
using MQTTnet.Protocol;

namespace Lookout.Link;

public sealed class MqttSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string? Username { get; set; }
    public string? Password { get; set; }
}

/// <summary>
/// A small MQTT client: connects (and reconnects with backoff), re-subscribes after each
/// reconnect, and hands every message to one callback as (topic, payload bytes).
/// Messages use QoS 1, at-least-once, so handlers must tolerate the odd duplicate.
/// </summary>
public sealed class MqttConnection(MqttSettings settings, string clientId, Action<string>? log = null) : IAsyncDisposable
{
    private readonly MqttClientFactory _factory = new();
    private readonly List<string> _subscriptions = [];
    private IMqttClient? _client;
    private CancellationTokenSource? _stop;
    private readonly string _clientId = $"{clientId}-{Guid.NewGuid().ToString("N")[..8]}";

    public Func<string, byte[], Task>? OnMessage { get; set; }
    public bool IsConnected => _client?.IsConnected ?? false;

    public async Task StartAsync(IEnumerable<string> subscriptions, CancellationToken ct)
    {
        _subscriptions.AddRange(subscriptions);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            if (OnMessage is null) return;
            var payload = e.ApplicationMessage.Payload.ToArray();
            await OnMessage(e.ApplicationMessage.Topic, payload);
        };
        _client.DisconnectedAsync += async _ =>
        {
            if (_stop.IsCancellationRequested) return;
            log?.Invoke("MQTT disconnected; reconnecting");
            await ConnectWithRetryAsync(_stop.Token);
        };
        await ConnectWithRetryAsync(_stop.Token);
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(settings.Host, settings.Port)
                    // Unique per connection: in containers every process is PID 1, and two clients
                    // sharing an id make the broker drop one for the other (SessionTakenOver).
                    .WithClientId(_clientId)
                    .WithCleanSession()
                    .WithTimeout(TimeSpan.FromSeconds(5));
                if (settings.Username is not null) options.WithCredentials(settings.Username, settings.Password);
                await _client!.ConnectAsync(options.Build(), ct);
                if (_subscriptions.Count > 0)
                {
                    var sub = _factory.CreateSubscribeOptionsBuilder();
                    foreach (var t in _subscriptions) sub.WithTopicFilter(f => f.WithTopic(t).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
                    await _client.SubscribeAsync(sub.Build(), ct);
                }
                log?.Invoke($"MQTT connected to {settings.Host}:{settings.Port}");
                return;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                log?.Invoke($"MQTT connect failed ({e.Message}); retrying in {delay.TotalSeconds:0} s");
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }

    public async Task PublishAsync(string topic, byte[] payload, CancellationToken ct = default)
    {
        if (_client is not { IsConnected: true }) throw new InvalidOperationException("MQTT is not connected.");
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();
        await _client.PublishAsync(msg, ct);
    }

    /// <summary>Publish, or log and return false if the broker is unreachable right now.</summary>
    public async Task<bool> TryPublishAsync(string topic, byte[] payload, CancellationToken ct = default)
    {
        try
        {
            await PublishAsync(topic, payload, ct);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or MQTTnet.Exceptions.MqttCommunicationException)
        {
            log?.Invoke($"MQTT publish to {topic} failed: {e.Message}");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop?.Cancel();
        if (_client is { IsConnected: true }) await _client.DisconnectAsync();
        _client?.Dispose();
    }
}
