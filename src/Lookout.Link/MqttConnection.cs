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
/// A small MQTT client. One background loop owns the connection: it connects, subscribes, and checks
/// once a second that it is still connected, reconnecting with backoff when it is not. Nothing else
/// starts a connect, so a broker outage cannot set off overlapping reconnect attempts.
/// <see cref="StartAsync"/> returns at once; callers that must wait for the broker use
/// <see cref="WaitConnectedAsync"/>. Messages use QoS 1, at-least-once, so handlers must tolerate
/// the odd duplicate.
/// </summary>
public sealed class MqttConnection(MqttSettings settings, string clientId, Action<string>? log = null) : IAsyncDisposable
{
    private readonly MqttClientFactory _factory = new();
    private readonly List<string> _subscriptions = [];
    // Unique per connection: in containers every process is PID 1, and two clients sharing an id
    // make the broker drop one for the other (SessionTakenOver).
    private readonly string _clientId = $"{clientId}-{Guid.NewGuid().ToString("N")[..8]}";
    private readonly TaskCompletionSource _firstConnect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IMqttClient? _client;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public Func<string, byte[], Task>? OnMessage { get; set; }
    public bool IsConnected => _client?.IsConnected ?? false;

    public Task StartAsync(IEnumerable<string> subscriptions, CancellationToken ct)
    {
        if (_loop is not null) throw new InvalidOperationException("Already started.");
        _subscriptions.AddRange(subscriptions);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            if (OnMessage is null) return;
            await OnMessage(e.ApplicationMessage.Topic, e.ApplicationMessage.Payload.ToArray());
        };
        _client.DisconnectedAsync += e =>
        {
            // Failed connect attempts also raise this event; only report real drops. The loop reconnects.
            if (e.ClientWasConnected && !_stop.IsCancellationRequested) log?.Invoke($"MQTT disconnected ({e.Reason})");
            return Task.CompletedTask;
        };
        _loop = Task.Run(() => KeepConnectedAsync(_stop.Token));
        return Task.CompletedTask;
    }

    /// <summary>Completes when the first connection is up (or throws on timeout/cancel).</summary>
    public Task WaitConnectedAsync(CancellationToken ct) => _firstConnect.Task.WaitAsync(ct);

    private async Task KeepConnectedAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            if (_client!.IsConnected)
            {
                delay = TimeSpan.FromSeconds(1);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(settings.Host, settings.Port)
                    .WithClientId(_clientId)
                    .WithCleanSession()
                    .WithTimeout(TimeSpan.FromSeconds(5));
                if (settings.Username is not null) options.WithCredentials(settings.Username, settings.Password);
                await _client.ConnectAsync(options.Build(), ct);
                if (_subscriptions.Count > 0)
                {
                    var sub = _factory.CreateSubscribeOptionsBuilder();
                    foreach (var t in _subscriptions) sub.WithTopicFilter(f => f.WithTopic(t).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce));
                    await _client.SubscribeAsync(sub.Build(), ct);
                }
                log?.Invoke($"MQTT connected to {settings.Host}:{settings.Port}");
                _firstConnect.TrySetResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log?.Invoke($"MQTT connect failed ({e.Message}); retrying in {delay.TotalSeconds:0} s");
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
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
        if (_loop is not null) { try { await _loop; } catch (OperationCanceledException) { } }
        if (_client is { IsConnected: true }) await _client.DisconnectAsync();
        _client?.Dispose();
    }
}
