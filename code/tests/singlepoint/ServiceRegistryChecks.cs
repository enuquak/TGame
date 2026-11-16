using System.Text;
using org.apache.zookeeper;
using TGame;
using TGame.Server.Engine;

internal static class ServiceRegistryChecks
{
    private static readonly ServerProcess Process = new(ProcessType.Router, 1);
    private const string Address = "http://127.0.0.1:12345";
    private const string ReplacementAddress = "http://127.0.0.1:12346";
    private const int TimeoutSeconds = 10;

    private sealed class WatcherStub : Watcher
    {
        public override Task process(WatchedEvent evt) => Task.CompletedTask;
    }

    public static async Task RunAsync(string endpoint, string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        var stop = deadline.Token;
        var path = root + "/server-" + Process.Name;
        var owner = new ZooKeeper(endpoint, TimeoutSeconds * 1000, new WatcherStub());
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Router] = 1;
        await using var registry = new ServiceRegistry(new ServerGroup(counts), endpoint, root);
        try
        {
            // Never replace a node owned by another session, including another copy of this test.
            await owner.createAsync(path, Encoding.UTF8.GetBytes(Address), ZooDefs.Ids.OPEN_ACL_UNSAFE,
                CreateMode.EPHEMERAL).WaitAsync(stop);

            bool rejected = false;
            try
            {
                await registry.RegisterAsync(Process, ReplacementAddress, stop);
            }
            catch (InvalidOperationException error) when (error.InnerException is KeeperException.NodeExistsException)
            {
                rejected = error.Message.Contains(path, StringComparison.Ordinal);
            }

            if (!rejected)
            {
                throw new Exception("An existing fixed service path must fail startup immediately with the conflicting path.");
            }

            var data = await owner.getDataAsync(path).WaitAsync(stop);
            if (Encoding.UTF8.GetString(data.Data) != Address)
            {
                throw new Exception("A failed registration changed the existing service address.");
            }
        }
        finally
        {
            await owner.closeAsync();
        }

        await registry.RegisterAsync(Process, ReplacementAddress, stop);
        if (await registry.ResolveAsync(Process.Name, stop) != ReplacementAddress)
        {
            throw new Exception("Registration and discovery must use the same fixed service path.");
        }

        Console.WriteLine("PASS fixed service paths, duplicate startup rejection, node ownership and registration after owner exit.");
    }
}
