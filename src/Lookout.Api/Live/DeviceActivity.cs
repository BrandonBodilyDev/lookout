using System.Collections.Concurrent;

namespace Lookout.Api.Live;

/// <param name="BatteryPercent">From the device's latest position report; null if it has only sent acks.</param>
public sealed record DeviceHeard(string DeviceId, DateTimeOffset LastHeard, int? BatteryPercent, long Messages);

/// <summary>
/// When each link device was last heard, assigned or not. Kept in memory: it answers "is this device
/// alive right now?" and "which unassigned device just switched on?", not history (the fixes table and
/// audit trail hold that). Empty after a restart until devices report again.
/// </summary>
public sealed class DeviceActivity(TimeProvider clock)
{
    /// <summary>Bound on remembered devices, so a misbehaving publisher cannot grow memory without limit.</summary>
    public const int MaxDevices = 10_000;
    private readonly ConcurrentDictionary<string, DeviceHeard> _heard = new();

    public void Heard(string deviceId, int? batteryPercent)
    {
        var now = clock.GetUtcNow();
        _heard.AddOrUpdate(deviceId,
            id => new DeviceHeard(id, now, batteryPercent, 1),
            (_, d) => new DeviceHeard(d.DeviceId, now, batteryPercent ?? d.BatteryPercent, d.Messages + 1));
        if (_heard.Count > MaxDevices)
            foreach (var old in _heard.Values.OrderBy(d => d.LastHeard).Take(_heard.Count - MaxDevices))
                _heard.TryRemove(old.DeviceId, out _);
    }

    public DeviceHeard? Get(string deviceId) => _heard.GetValueOrDefault(deviceId);

    /// <summary>Devices heard within <paramref name="window"/>, most recent first.</summary>
    public IReadOnlyList<DeviceHeard> HeardWithin(TimeSpan window)
    {
        var since = clock.GetUtcNow() - window;
        return _heard.Values.Where(d => d.LastHeard >= since).OrderByDescending(d => d.LastHeard).ToList();
    }
}
