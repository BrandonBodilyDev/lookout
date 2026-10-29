using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lookout.Api;

/// <summary>
/// Lookout's own metrics and traces, on the standard .NET APIs (System.Diagnostics.Metrics and
/// ActivitySource), exported over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set. Names follow
/// OpenTelemetry conventions: lowercase, dot-separated, with units.
/// </summary>
public static class Telemetry
{
    public const string Name = "Lookout";

    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    public static readonly Histogram<double> TickDuration =
        Meter.CreateHistogram<double>("lookout.engine.tick.duration", "ms", "Time to evaluate every crew of one incident once.");

    public static readonly Counter<long> AlertsRaised =
        Meter.CreateCounter<long>("lookout.alerts.raised", "{alert}", "Alerts raised by the engine, by level.");

    public static readonly Counter<long> AlertsEscalated =
        Meter.CreateCounter<long>("lookout.alerts.escalated", "{alert}", "Warnings escalated to the IC for lack of acknowledgement.");

    public static readonly Counter<long> DownlinkBytes =
        Meter.CreateCounter<long>("lookout.link.downlink.bytes", "By", "Bytes handed to the crew downlink.");

    public static readonly Counter<long> DownlinkFailures =
        Meter.CreateCounter<long>("lookout.link.downlink.failures", "{message}", "Downlink publishes that failed and were requeued.");

    public static readonly Counter<long> UplinkMessages =
        Meter.CreateCounter<long>("lookout.link.uplink.messages", "{message}", "Crew uplink messages received, by kind.");

    public static readonly Histogram<double> AckLatency =
        Meter.CreateHistogram<double>("lookout.alerts.ack.latency", "s", "Wall-clock seconds from first send to acknowledgement.");
}
