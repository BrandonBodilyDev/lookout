using Lookout.Api.Data;

namespace Lookout.Api.Live;

/// <summary>
/// What the live runner last reported about itself, for the readiness check. The process can be up
/// (liveness) while the loop is stalled, the broker unreachable or the device directory unloaded, and
/// any of those means crews get no warnings. A readiness probe or monitor should page on this.
/// </summary>
public sealed class LiveHealth
{
    private long _lastTickTicks;
    public DateTimeOffset? LastTick => Interlocked.Read(ref _lastTickTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;
    public void Ticked(DateTimeOffset at) => Interlocked.Exchange(ref _lastTickTicks, at.UtcTicks);

    /// <summary>Null in loopback mode (no broker configured).</summary>
    public Func<bool>? BrokerConnected { get; set; }
    public volatile bool DevicesLoaded;

    /// <summary>A loop pass normally completes every tick; allow a few slow ones before calling it stalled.</summary>
    public static TimeSpan StallAfter(LiveOptions o) => TimeSpan.FromSeconds(Math.Max(10, 5 * o.TickSeconds));

    public sealed record Check(string Name, bool Ok, string Detail);

    /// <summary>Can the API reach its database within 3 seconds?</summary>
    public static async Task<Check> DatabaseCheckAsync(IServiceProvider sp, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var scope = sp.CreateAsyncScope();
            var ok = await scope.ServiceProvider.GetRequiredService<LookoutDb>().Database.CanConnectAsync(timeout.Token);
            return new("database", ok, ok ? "reachable" : "unreachable");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new("database", false, e.GetType().Name);
        }
    }

    public IReadOnlyList<Check> Evaluate(DateTimeOffset now, LiveOptions options)
    {
        var checks = new List<Check>();
        var stallAfter = StallAfter(options);
        checks.Add(LastTick is { } t
            ? new Check("loop", now - t <= stallAfter, $"last pass {(now - t).TotalSeconds:0.0} s ago (stalled after {stallAfter.TotalSeconds:0} s)")
            : new Check("loop", false, "no pass completed yet"));
        checks.Add(new Check("devices", DevicesLoaded, DevicesLoaded ? "directory loaded" : "directory not loaded: crew uplinks are being dropped"));
        if (BrokerConnected is { } connected)
            checks.Add(connected() ? new Check("broker", true, "connected") : new Check("broker", false, "disconnected: alerts are queued, not delivered"));
        return checks;
    }
}
