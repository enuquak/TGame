using System.Text;
using System.Text.Json;
using org.apache.zookeeper;

namespace TGame.PingPong;

internal static class Topology
{
    // One instance per role for this smoke run. Each pair opens one stream.
    public static readonly Dictionary<string, string[]> Dials = new()
    {
        ["Gate"] = ["Login", "Base"], ["Login"] = ["Router"], ["Base"] = ["Router", "DBMgr"],
        ["Scene"] = ["Router"], ["DBMgr"] = ["Router"], ["Profile"] = ["Router"], ["GM"] = ["Router"], ["Router"] = []
    };
    public static readonly Dictionary<string, string> Entities = new()
    {
        ["Login"] = "login-1", ["Base"] = "body-1", ["Scene"] = "soul-1", ["DBMgr"] = "db-probe-1",
        ["Profile"] = "profile-1"
    };
    public static int Degree(string role) => Dials[role].Length + Dials.Values.Count(v => v.Contains(role));
}

internal static class TraceLog
{
    public static void Event(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null) =>
        Console.WriteLine(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, ev, role, pid = Environment.ProcessId, peer, id, entity, link, detail }));
}

internal sealed class ProbeRegistry(Guid run) : IAsyncDisposable
{
    private sealed class Changed : Watcher
    {
        public TaskCompletionSource Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task process(WatchedEvent evt) { Signal.TrySetResult(); return Task.CompletedTask; }
    }
    private const string Root = "/tgame/test";
    private readonly ZooKeeper zk = new("127.0.0.1:2181", 10000, new Changed());
    private readonly string prefix = $"pingpong-{run:N}-";

    public async Task<Dictionary<string, string>> RegisterAndDiscoverAsync(string role, string address)
    {
        await zk.createAsync($"{Root}/{prefix}{role}", Encoding.UTF8.GetBytes(address),
            ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL);
        TraceLog.Event("ZK_REGISTER", role, detail: address);
        while (true)
        {
            var changed = new Changed();
            var names = (await zk.getChildrenAsync(Root, changed)).Children.ToHashSet();
            if (Topology.Dials.Keys.All(r => names.Contains(prefix + r))) break;
            await changed.Signal.Task;
        }
        var cache = new Dictionary<string, string>();
        foreach (var r in Topology.Dials.Keys)
        {
            var data = await zk.getDataAsync($"{Root}/{prefix}{r}");
            var uri = new Uri(Encoding.UTF8.GetString(data.Data));
            if (uri.Host != "127.0.0.1" || uri.Scheme != (r == "Gate" ? "https" : "http"))
                throw new InvalidDataException($"Unexpected probe endpoint for {r}.");
            cache.Add(r, uri.GetLeftPart(UriPartial.Authority));
        }
        TraceLog.Event("ZK_DISCOVER", role, detail: string.Join(",", cache.Keys));
        return cache;
    }
    public async ValueTask DisposeAsync() => await zk.closeAsync();
}
