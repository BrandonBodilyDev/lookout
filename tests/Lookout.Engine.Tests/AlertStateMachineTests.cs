using Lookout.Engine.Workflow;

namespace Lookout.Engine.Tests;

public class AlertStateMachineTests
{
    [Theory]
    [InlineData(AlertState.Queued, AlertAction.Send, AlertState.Sent)]
    [InlineData(AlertState.Sent, AlertAction.Acknowledge, AlertState.Acknowledged)]
    [InlineData(AlertState.Sent, AlertAction.Escalate, AlertState.Escalated)]
    [InlineData(AlertState.Queued, AlertAction.Escalate, AlertState.Escalated)] // never delivered
    [InlineData(AlertState.Escalated, AlertAction.Acknowledge, AlertState.Acknowledged)]
    [InlineData(AlertState.Queued, AlertAction.Supersede, AlertState.Superseded)]
    [InlineData(AlertState.Escalated, AlertAction.Cancel, AlertState.Cancelled)]
    public void Allowed_transitions(AlertState from, AlertAction action, AlertState to) =>
        Assert.Equal(to, AlertStateMachine.Apply(from, action));

    [Theory]
    [InlineData(AlertState.Queued, AlertAction.Acknowledge)]   // cannot ack what was never sent
    [InlineData(AlertState.Acknowledged, AlertAction.Cancel)]  // terminal states are final
    [InlineData(AlertState.Cancelled, AlertAction.Acknowledge)]
    [InlineData(AlertState.Superseded, AlertAction.Send)]
    [InlineData(AlertState.Escalated, AlertAction.Escalate)]
    public void Refused_transitions(AlertState from, AlertAction action) =>
        Assert.Throws<InvalidAlertTransitionException>(() => AlertStateMachine.Apply(from, action));

    [Fact]
    public void Terminal_states_allow_nothing()
    {
        foreach (var s in Enum.GetValues<AlertState>().Where(AlertStateMachine.IsTerminal))
            Assert.Empty(AlertStateMachine.Allowed(s));
    }
}
