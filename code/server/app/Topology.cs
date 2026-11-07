using System.Text;
using org.apache.zookeeper;
using TGame.Server.Logic;

namespace TGame.PingPong;

internal static class Topology
{
    // One instance per role for this smoke run. Each pair opens one stream.
    public static readonly Dictionary<string, string[]> Dials = new()
    {
        [nameof(ProcessType.Gate)] = [nameof(ProcessType.Login), nameof(ProcessType.Base)],
        [nameof(ProcessType.Login)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.Base)] = [nameof(ProcessType.Router), nameof(ProcessType.DBMgr)],
        [nameof(ProcessType.Scene)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.DBMgr)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.Profile)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.GM)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.Router)] = []
    };

    public static readonly Dictionary<string, string> Entities = new()
    {
        [nameof(ProcessType.Login)] = "login-1",
        [nameof(ProcessType.Base)] = "body-1",
        [nameof(ProcessType.Scene)] = "soul-1",
        [nameof(ProcessType.DBMgr)] = "db-probe-1",
        [nameof(ProcessType.Profile)] = "profile-1"
    };

    public static int Degree(string role) => Dials[role].Length + Dials.Values.Count(v => v.Contains(role));
}

internal sealed class ProbeRegistry(Guid run) : IAsyncDisposable
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
    private readonly ZooKeeper zk = new("127.0.0.1:2181", 10000, new Changed());
    private readonly string prefix = $"pingpong-{run:N}-";

    public async Task RegisterAsync(string role, string address, bool consoleMode, CancellationToken stop)
    {
        var path = $"{Root}/{prefix}{role}";
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
                    await changed.Signal.Task.WaitAsync(stop);
            }
        }

        TraceLog.Event("ZK_REGISTER", role, detail: address);
        var names = (await zk.getChildrenAsync(Root).WaitAsync(stop)).Children;
        TraceLog.Event("ZK_DISCOVER", role, detail: string.Join(",", Topology.Dials.Keys.Where(r => names.Contains(prefix + r))));
    }

    public async Task<string> ResolveAsync(string peer, CancellationToken stop)
    {
        var path = $"{Root}/{prefix}{peer}";
        while (true)
        {
            try
            {
                var data = await zk.getDataAsync(path).WaitAsync(stop);
                var uri = new Uri(Encoding.UTF8.GetString(data.Data));
                if (uri.Host != "127.0.0.1" || uri.Scheme != (peer == nameof(ProcessType.Gate) ? "https" : "http"))
                    throw new InvalidDataException($"Unexpected probe endpoint for {peer}.");

                return uri.GetLeftPart(UriPartial.Authority);
            }
            catch (KeeperException.NoNodeException)
            {
                var changed = new Changed();
                if (await zk.existsAsync(path, changed).WaitAsync(stop) == null)
                    await changed.Signal.Task.WaitAsync(stop);
            }
        }
    }

    public async ValueTask DisposeAsync() => await zk.closeAsync();
}
