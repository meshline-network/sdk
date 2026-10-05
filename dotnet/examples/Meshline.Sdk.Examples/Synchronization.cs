using Meshline.Models.Client;
using Meshline.Models.Protocol;

namespace Meshline.Examples;

public static class Synchronization
{
    #region synchronize
    public static async Task<ResourceSyncStatus> RefreshGroupAsync(
        MeshlineClient client, string relayId, GroupRef group,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await client.MessageManager.SynchronizeAsync(relayId, deadline.Token);
        return await client.GroupManager.SynchronizeAsync(group, deadline.Token);
    }
    #endregion
}
