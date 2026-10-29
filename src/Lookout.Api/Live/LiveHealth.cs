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
    private readonly object _passLock = new();
    private readonly Queue<(DateTimeOffset At, double Ms)> _passes = new();

    /// <summary>A loop pass finished at <paramref name="at"/> after <paramref name="passMs"/> milliseconds.</summary>
    public void Ticked(DateTimeOffset at, double passMs = 0)
    {
        Interlocked.Exchange(ref _lastTickTicks, at.UtcTicks);
        lock (_passLock)
        {
            _passes.Enqueue((at, passMs));
            while (_passes.Count > 0 && at - _passes.Peek().At > TimeSpan.FromMinutes(1)) _passes.Dequeue();
        }
    }

    /// <summary>Slowest loop pass in the last minute, in milliseconds; null before the first pass.</summary>
    public double? SlowestPassMs
    {
        get { lock (_passLock) return _passes.Count == 0 ? null : _passes.Max(p => p.Ms); }
    }

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
            ? new Check("loop", now - t <= stallAfter,
                $"last pass {(now - t).TotalSeconds:0.0} s ago (stalled after {stallAfter.TotalSeconds:0} s); slowest pass in the last minute {SlowestPassMs ?? 0:0} ms")
            : new Check("loop", false, "no pass completed yet"));
        checks.Add(new Check("devices", DevicesLoaded, DevicesLoaded ? "directory loaded" : "directory not loaded: crew uplinks are being dropped"));
        if (BrokerConnected is { } connected)
            checks.Add(connected() ? new Check("broker", true, "connected") : new Check("broker", false, "disconnected: alerts are queued, not delivered"));
        return checks;
    }
}
