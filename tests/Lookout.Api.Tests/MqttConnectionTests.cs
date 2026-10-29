using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using Lookout.Link;

namespace Lookout.Api.Tests;

/// <summary>
/// Regression for a reconnect storm: with the broker down, the old client spawned overlapping
/// reconnect loops (about 168,000 log lines in 8 s) and StartAsync never returned.
/// </summary>
public class MqttConnectionTests
{
    [Fact]
    public async Task Starts_without_a_broker_retries_calmly_and_connects_when_it_appears()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = FreePort();
        var log = new ConcurrentQueue<string>();
        await using var client = new MqttConnection(new MqttSettings { Host = "127.0.0.1", Port = port }, "test", log.Enqueue);
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnMessage = (_, payload) => { received.TrySetResult(payload); return Task.CompletedTask; };

        var start = client.StartAsync(["lookout/test"], ct);
        Assert.True(start.IsCompleted, "StartAsync must not wait for the broker");

        await Task.Delay(TimeSpan.FromSeconds(4), ct);
        Assert.False(client.IsConnected);
        Assert.InRange(log.Count, 1, 6); // backoff of 1, 2, 4 s: a handful of attempts, not a storm

        await using var broker = new ContainerBuilder("eclipse-mosquitto:2.1.2-alpine")
            .WithCommand("mosquitto", "-c", "/mosquitto-no-auth.conf")
            .WithPortBinding(port, 1883)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("running"))
            .Build();
        await broker.StartAsync(ct);

        await client.WaitConnectedAsync(ct).WaitAsync(TimeSpan.FromSeconds(40), ct);
        await Task.Delay(500, ct); // let the subscription settle
        Assert.True(await client.TryPublishAsync("lookout/test", [1, 2, 3], ct));
        Assert.Equal([1, 2, 3], await received.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
