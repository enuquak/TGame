using System.Text;
using System.Net;
using org.apache.zookeeper;

namespace TGame.Server;

internal sealed class ServiceRegistry(Guid run) : IAsyncDisposable
{
    private sealed class Changed : Watcher
    {
        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task process(WatchedEvent evt)
        {
            Signal.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private const int SessionTimeoutMilliseconds = 10000;
    private readonly string root = ServerSettings.Current.Value.DiscoveryRoot;
    private readonly ZooKeeper zk = new(ServerSettings.Current.Value.ZooKeeper, SessionTimeoutMilliseconds, new Changed());
    private readonly string prefix = $"server-{run:N}-";

    public async Task RegisterAsync(string role, string address, bool consoleMode, CancellationToken stop)
    {
        var path = $"{root}/{prefix}{role}";
        while (true)
        {
            try
            {
                await zk.createAsync(path, Encoding.UTF8.GetBytes(address), ZooDefs.Ids.OPEN_ACL_UNSAFE,
                    CreateMode.EPHEMERAL).WaitAsync(stop);
                break;
            }
            catch (KeeperException.NodeExistsException) when (consoleMode)
            {
                // After a forced stop, the previous instance's ephemeral node can outlive its process.
                var changed = new Changed();
                if (await zk.existsAsync(path, changed).WaitAsync(stop) != null)
                {
                    await changed.Signal.Task.WaitAsync(stop);
                }
            }
        }

        TraceLog.Event(ServerLogEvents.ZkRegister, role, detail: address);
        var names = (await zk.getChildrenAsync(root).WaitAsync(stop)).Children;
        TraceLog.Event(ServerLogEvents.ZkDiscover, role, detail: string.Join(",", Topology.Dials.Keys.Where(r => names.Contains(prefix + r))));
    }

    public async Task<string> ResolveAsync(string peer, CancellationToken stop)
    {
        var path = $"{root}/{prefix}{peer}";
        while (true)
        {
            try
            {
                var data = await zk.getDataAsync(path).WaitAsync(stop);
                var uri = new Uri(Encoding.UTF8.GetString(data.Data));
                var expectedScheme = Uri.UriSchemeHttp;
                if (peer == nameof(ProcessType.Gate))
                {
                    expectedScheme = Uri.UriSchemeHttps;
                }

                if (uri.Host != IPAddress.Loopback.ToString() || uri.Scheme != expectedScheme)
                {
                    throw new InvalidDataException($"Unexpected server endpoint for {peer}.");
                }

                return uri.GetLeftPart(UriPartial.Authority);
            }
            catch (KeeperException.NoNodeException)
            {
                var changed = new Changed();
                if (await zk.existsAsync(path, changed).WaitAsync(stop) == null)
                {
                    await changed.Signal.Task.WaitAsync(stop);
                }
            }
        }
    }

    public async ValueTask DisposeAsync() => await zk.closeAsync();
}
