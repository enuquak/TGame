using System.Text;
using org.apache.zookeeper;
using TGame.Server.Engine;

internal static class ServiceRegistryChecks
{
    private const string Role = "RegistryCheck";
    private const string Path = "/tgame/test/server-" + Role;
    private const string Address = "http://127.0.0.1:12345";
    private const string ReplacementAddress = "http://127.0.0.1:12346";
    private const int TimeoutSeconds = 10;

    private sealed class WatcherStub : Watcher
    {
        public override Task process(WatchedEvent evt) => Task.CompletedTask;
    }

    public static async Task RunAsync(string endpoint)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));
        var stop = deadline.Token;
        var owner = new ZooKeeper(endpoint, TimeoutSeconds * 1000, new WatcherStub());
        await using var registry = new ServiceRegistry(endpoint);
        try
        {
            // Never replace a node owned by another session, including another copy of this test.
            await owner.createAsync(Path, Encoding.UTF8.GetBytes(Address), ZooDefs.Ids.OPEN_ACL_UNSAFE,
                CreateMode.EPHEMERAL).WaitAsync(stop);

            bool rejected = false;
            try
            {
                await registry.RegisterAsync(Role, ReplacementAddress, stop);
            }
            catch (InvalidOperationException error) when (error.InnerException is KeeperException.NodeExistsException)
            {
                rejected = error.Message.Contains(Path, StringComparison.Ordinal);
            }

            if (!rejected)
            {
                throw new Exception("An existing fixed service path must fail startup immediately with the conflicting path.");
            }

            var data = await owner.getDataAsync(Path).WaitAsync(stop);
            if (Encoding.UTF8.GetString(data.Data) != Address)
            {
                throw new Exception("A failed registration changed the existing service address.");
            }
        }
        finally
        {
            await owner.closeAsync();
        }

        await registry.RegisterAsync(Role, ReplacementAddress, stop);
        if (await registry.ResolveAsync(Role, stop) != ReplacementAddress)
        {
            throw new Exception("Registration and discovery must use the same fixed service path.");
        }

        Console.WriteLine("PASS fixed service paths, duplicate startup rejection, node ownership and registration after owner exit.");
    }
}
