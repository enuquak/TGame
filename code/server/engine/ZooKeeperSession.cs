using org.apache.zookeeper;

namespace TGame.Server.Engine;

internal sealed class ZooKeeperSession : Watcher
{
    private volatile bool connected;
    private volatile bool failed;
    internal TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Lost { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override Task process(WatchedEvent evt)
    {
        StateChanged(evt.getState());
        return Task.CompletedTask;
    }

    internal void StateChanged(Event.KeeperState state)
    {
        if (state == Event.KeeperState.SyncConnected && !failed)
        {
            connected = true;
            Connected.TrySetResult();
            return;
        }

        if (state is Event.KeeperState.Expired or Event.KeeperState.AuthFailed ||
            state == Event.KeeperState.Disconnected && connected)
        {
            Invalidate();
        }
    }

    internal void Invalidate()
    {
        failed = true;
        connected = false;
        Connected.TrySetResult();
        Lost.TrySetResult();
    }

    internal void CheckActive()
    {
        if (!connected || failed)
        {
            throw new InvalidOperationException("The ZooKeeper session is unavailable; restart this process to acquire a new identity.");
        }
    }
}
