using System.Text;
using System.Net;
using org.apache.zookeeper;

namespace TGame.Server.Engine;

public sealed class ServiceRegistry(Guid run, ServerGroup group, string endpoint) : IAsyncDisposable
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

    private const string Root = "/tgame/test";
    private const int SessionTimeoutMilliseconds = 10000;
    private readonly ZooKeeper zk = new(endpoint, SessionTimeoutMilliseconds, new Changed());
    private readonly string prefix = $"server-{run:N}-";

    public async Task RegisterAsync(ServerProcess process, string address, bool consoleMode, CancellationToken stop)
    {
        var path = $"{Root}/{prefix}{process.Name}";
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

        TraceLog.Event(ServerLogEvents.ZkRegister, process.Role, detail: address);
        var names = (await zk.getChildrenAsync(Root).WaitAsync(stop)).Children;
        var registered = new List<string>();
        foreach (var instance in group.Processes)
        {
            if (names.Contains(prefix + instance.Name))
            {
                registered.Add(instance.Name);
            }
        }

        TraceLog.Event(ServerLogEvents.ZkDiscover, process.Role, detail: string.Join(",", registered));
    }

    public async Task<string> ResolveAsync(string peer, CancellationToken stop)
    {
        var process = group.GetProcess(peer);
        var path = $"{Root}/{prefix}{peer}";
        while (true)
        {
            try
            {
                var data = await zk.getDataAsync(path).WaitAsync(stop);
                var uri = new Uri(Encoding.UTF8.GetString(data.Data));
                var expectedScheme = Uri.UriSchemeHttp;
                if (process.Type == ProcessType.Gate)
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
