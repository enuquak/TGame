using System.Text;
using System.Net;
using org.apache.zookeeper;

namespace TGame.Server.Engine;

public sealed class ServiceRegistry(ServerGroup group, string endpoint, string root) : IAsyncDisposable
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

    private const string NodePrefix = "server-";
    private const int SessionTimeoutMilliseconds = 10000;
    private readonly ZooKeeper zk = new(endpoint, SessionTimeoutMilliseconds, new Changed());

    public async Task RegisterAsync(ServerProcess process, string address, CancellationToken stop)
    {
        var path = $"{root}/{NodePrefix}{process.Name}";
        try
        {
            await zk.createAsync(path, Encoding.UTF8.GetBytes(address), ZooDefs.Ids.OPEN_ACL_UNSAFE,
                CreateMode.EPHEMERAL).WaitAsync(stop);
        }
        catch (KeeperException.NodeExistsException error)
        {
            throw new InvalidOperationException($"Cannot start {process.Name}: ZooKeeper node '{path}' already exists.", error);
        }

        TraceLog.Event(ServerLogEvents.ZkRegister, process.Role, detail: address);
        var names = (await zk.getChildrenAsync(root).WaitAsync(stop)).Children;
        var registered = new List<string>();
        foreach (var instance in group.Processes)
        {
            if (names.Contains(NodePrefix + instance.Name))
            {
                registered.Add(instance.Name);
            }
        }

        TraceLog.Event(ServerLogEvents.ZkDiscover, process.Role, detail: string.Join(",", registered));
    }

    public async Task<string> ResolveAsync(string peer, CancellationToken stop)
    {
        var process = group.GetProcess(peer);
        var path = $"{root}/{NodePrefix}{peer}";
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
