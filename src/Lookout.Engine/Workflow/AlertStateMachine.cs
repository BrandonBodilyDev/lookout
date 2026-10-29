namespace Lookout.Engine.Workflow;

/// <summary>Lifecycle of one alert message to one crew.</summary>
public enum AlertState
{
    /// <summary>Raised by the engine, waiting for the link governor.</summary>
    Queued,
    /// <summary>Published on the crew's downlink; waiting for acknowledgement if one is required.</summary>
    Sent,
    /// <summary>No acknowledgement within the timeout; the incident commander has been notified.</summary>
    Escalated,
    Acknowledged,
    /// <summary>Replaced by a newer alert for the same crew before it was acknowledged.</summary>
    Superseded,
    /// <summary>Withdrawn by the incident commander, with a reason.</summary>
    Cancelled,
}

public enum AlertAction { Send, Acknowledge, Escalate, Supersede, Cancel }

/// <summary>
/// The alert workflow, after inReach SOS semantics: a warning repeats until someone acknowledges
/// it; silence escalates to the IC rather than being dropped; withdrawal is an explicit,
/// recorded act. Every transition is listed here and nowhere else; anything not listed is refused.
/// Who may perform an action (crew vs IC vs system) is checked by the caller.
/// </summary>
public static class AlertStateMachine
{
    private static readonly Dictionary<(AlertState, AlertAction), AlertState> Transitions = new()
    {
        [(AlertState.Queued, AlertAction.Send)] = AlertState.Sent,
        [(AlertState.Queued, AlertAction.Supersede)] = AlertState.Superseded,
        [(AlertState.Queued, AlertAction.Cancel)] = AlertState.Cancelled,
        // Never delivered (no link device, broker down): the IC must hear about it just as for an unanswered one.
        [(AlertState.Queued, AlertAction.Escalate)] = AlertState.Escalated,

        [(AlertState.Sent, AlertAction.Acknowledge)] = AlertState.Acknowledged,
        [(AlertState.Sent, AlertAction.Escalate)] = AlertState.Escalated,
        [(AlertState.Sent, AlertAction.Supersede)] = AlertState.Superseded,
        [(AlertState.Sent, AlertAction.Cancel)] = AlertState.Cancelled,

        [(AlertState.Escalated, AlertAction.Acknowledge)] = AlertState.Acknowledged,
        [(AlertState.Escalated, AlertAction.Supersede)] = AlertState.Superseded,
        [(AlertState.Escalated, AlertAction.Cancel)] = AlertState.Cancelled,
    };

    public static bool TryApply(AlertState from, AlertAction action, out AlertState to) =>
        Transitions.TryGetValue((from, action), out to);

    public static AlertState Apply(AlertState from, AlertAction action) =>
        TryApply(from, action, out var to)
            ? to
            : throw new InvalidAlertTransitionException(from, action);

    public static bool IsTerminal(AlertState s) =>
        s is AlertState.Acknowledged or AlertState.Superseded or AlertState.Cancelled;

    public static IReadOnlyList<AlertAction> Allowed(AlertState s) =>
        Transitions.Keys.Where(k => k.Item1 == s).Select(k => k.Item2).ToList();
}

public sealed class InvalidAlertTransitionException(AlertState from, AlertAction action)
    : InvalidOperationException($"Cannot {action} an alert that is {from}.")
{
    public AlertState From { get; } = from;
    public AlertAction Action { get; } = action;
}
