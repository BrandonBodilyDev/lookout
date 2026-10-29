using Lookout.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Lookout.Api.Live;

/// <summary>
/// Live push to dashboards. A client joins an incident's group and then receives "frame" (the
/// map state, once a second) and "alert" (each alert state change). Joining returns the latest frame.
/// </summary>
[Authorize(Policy = Policies.Read)]
public sealed class IncidentHub(LiveSnapshots snapshots) : Hub
{
    public async Task<object?> Join(Guid incidentId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, incidentId.ToString());
        return snapshots.Get(incidentId);
    }

    public Task Leave(Guid incidentId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, incidentId.ToString());
}
