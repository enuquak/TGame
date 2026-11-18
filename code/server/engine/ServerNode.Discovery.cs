namespace TGame.Server.Engine;

public partial class ServerNode
{
    public Task MaintainConnectionsAsync(CancellationToken stop) =>
        MaintainConnectionsAsync(zooKeeper.WatchProcessesAsync(stop), stop);

    internal async Task MaintainConnectionsAsync(IAsyncEnumerable<IReadOnlyList<DiscoveredProcess>> snapshots,
        CancellationToken stop)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Dictionary<string, ConnectionWorker>(StringComparer.Ordinal);
        await using var discovery = snapshots.GetAsyncEnumerator(lifetime.Token);
        Task<bool>? next = null;
        try
        {
            while (true)
            {
                next = discovery.MoveNextAsync().AsTask();
                if (await Task.WhenAny(next, failure.Task) == failure.Task)
                {
                    await failure.Task;
                }

                if (!await next)
                {
                    throw new IOException("Process discovery ended unexpectedly.");
                }

                var targets = discovery.Current.Where(peer => configuration.Dials(process.Type, peer.Process.Type))
                    .ToDictionary(peer => peer.Process.Name, StringComparer.Ordinal);
                foreach (var (name, worker) in workers.ToArray())
                {
                    if (targets.TryGetValue(name, out var current) && current == worker.Peer)
                    {
                        continue;
                    }

                    worker.Stop.Cancel();
                    await worker.Task;
                    worker.Stop.Dispose();
                    workers.Remove(name);
                }

                foreach (var (name, peer) in targets)
                {
                    if (workers.ContainsKey(name))
                    {
                        continue;
                    }

                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    var task = RunAsync(peer, cancellation.Token);
                    workers.Add(name, new ConnectionWorker(peer, cancellation, task));
                }

                log.Debug(ServerLogEvents.ZkDiscover, Role, detail: string.Join(",", targets.Keys));
            }
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                if (next is not null)
                {
                    try
                    {
                        await next;
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                    }
                }
            }
            finally
            {
                await Task.WhenAll(workers.Values.Select(worker => worker.Task));
                foreach (var worker in workers.Values)
                {
                    worker.Stop.Dispose();
                }
            }
        }

        async Task RunAsync(DiscoveredProcess peer, CancellationToken token)
        {
            try
            {
                await MaintainLinkAsync(peer, token);
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                failure.TrySetException(error);
            }
        }
    }

    private sealed record ConnectionWorker(DiscoveredProcess Peer, CancellationTokenSource Stop, Task Task);
}
