using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode(string name, ServerSinglePoint singlePoint, ServerGroup group) : ServerNode(name, group)
{
    protected override bool Initialized => singlePoint.Initialized;

    public Task InitializeAsync(CancellationToken stop) => singlePoint.InitializeAsync(Role, id =>
    {
        if (!localEntities.TryAdd(id, 0))
        {
            throw new InvalidOperationException($"Duplicate local entity {id}.");
        }
    }, FirstLinks, stop);
}
