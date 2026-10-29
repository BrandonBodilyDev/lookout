namespace Lookout.Engine.Rules;

public enum AlertLevel { None = 0, Watch = 1, Warning = 2, Critical = 3 }

/// <summary>One rule's verdict for one crew at one instant.</summary>
/// <param name="Rule">Short rule id: "R1-CUTOFF", "WO11-UNBURNED-FUEL", "R3-COMMS".</param>
public sealed record Finding(string Rule, AlertLevel Level, string Summary);
