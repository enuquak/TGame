using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using org.apache.zookeeper;
using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal sealed class StartupPublicationChecks(TestServices services)
{
    public async Task RunAsync(CancellationToken stop)
    {
        await new StartupReadinessChecks(services).RunAsync(stop);
        var configured = ZooKeeperProxyChecks.LoadOptions();
        var options = configured with { DiscoveryRoot = configured.DiscoveryRoot + "/startup-check-" + Guid.NewGuid().ToString("N") };
        await using var proxy = new ZooKeeperProxy(options, services.Log);
        await proxy.InitializeAsync(stop);
        var owner = new ZooKeeper(options.Endpoint, options.SessionTimeoutMilliseconds, new WatcherStub());
        var scene = new ServerProcess(ProcessType.Scene, 1);
        var absent = new ServerProcess(ProcessType.Scene, 2);
        var persistent = new ServerProcess(ProcessType.Scene, 3);
        try
        {
            var process = await proxy.ClaimProcessAsync(ProcessType.Router, stop);
            Check(await proxy.IsProcessClaimedAsync(process.Name, stop),
                "A reserved ephemeral identity must be valid before its address is published.");
            Check(!await proxy.IsProcessClaimedAsync(absent.Name, stop), "An absent identity was accepted.");
            Check(!await proxy.IsProcessClaimedAsync(nameof(ProcessType.Scene), stop), "A type without an instance number was accepted.");
            await owner.createAsync(PathFor(persistent), [], ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.PERSISTENT).WaitAsync(stop);
            Check(!await proxy.IsProcessClaimedAsync(persistent.Name, stop), "A persistent node was accepted as a live process identity.");

            var configuration = TestNodes.LoadConfiguration(TestNodes.DefaultGroup);
            var node = new ServerNode(process, configuration, proxy, services.Ids, services.Log);
            await using var app = await StartAsync(node, proxy, stop);
            using var channel = GrpcChannel.ForAddress(app.Urls.Single());
            var client = new ServerLink.ServerLinkClient(channel);

            await owner.createAsync(PathFor(scene), [],
                ZooDefs.Ids.OPEN_ACL_UNSAFE, CreateMode.EPHEMERAL).WaitAsync(stop);
            await using var discovery = proxy.WatchProcessesAsync(stop).GetAsyncEnumerator(stop);
            Check(await discovery.MoveNextAsync() && discovery.Current.Count == 0,
                "An initializing process was discoverable before publishing its address.");

            Check(node.Ready, "Router was not ready after its initialization completed.");
            await proxy.RegisterProcessAsync(process, app.Urls.Single(), stop);
            do
            {
                Check(await discovery.MoveNextAsync(), "Discovery ended before address publication.");
            }
            while (discovery.Current.Count == 0);

            Check(discovery.Current.Count == 1 && discovery.Current[0].Process == process,
                "Publication exposed an initializing caller or lost the ready receiver.");
            using (var connection = client.Connect(Headers(scene.Name), cancellationToken: stop))
            {
                await connection.RequestStream.WriteAsync(new Frame { Hello = scene.Name }, stop);
                Check(await connection.ResponseStream.MoveNext(stop) && connection.ResponseStream.Current.Hello == process.Name,
                    "A ready process rejected an unpublished caller with a valid reserved identity.");
            }

            await ExpectUnavailableAsync(client, absent.Name, stop);
            await ExpectUnavailableAsync(client, persistent.Name, stop);
            await app.StopAsync(stop);
            Console.WriteLine("PASS startup publication: hidden reservations, claimed identity checks, readiness gate and ready endpoint discovery.");
        }
        finally
        {
            await proxy.DisposeAsync();
            try
            {
                var children = await owner.getChildrenAsync(options.DiscoveryRoot);
                foreach (var child in children.Children)
                {
                    await owner.deleteAsync(options.DiscoveryRoot + "/" + child);
                }

                await owner.deleteAsync(options.DiscoveryRoot);
            }
            finally
            {
                await owner.closeAsync();
            }
        }

        string PathFor(ServerProcess process) => options.DiscoveryRoot + "/server-" + process.Name;
    }

    private static async Task<WebApplication> StartAsync(ServerNode node, ZooKeeperProxy proxy, CancellationToken stop)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(node);
        builder.Services.AddSingleton(proxy);
        var app = builder.Build();
        app.MapGrpcService<LinkService>();
        await app.StartAsync(stop);
        return app;
    }

    private async Task ExpectUnavailableAsync(ServerLink.ServerLinkClient client, string peer, CancellationToken stop)
    {
        using var connection = client.Connect(Headers(peer), cancellationToken: stop);
        try
        {
            await connection.RequestStream.WriteAsync(new Frame { Hello = peer }, stop);
            await connection.ResponseStream.MoveNext(stop);
            throw new Exception("A connection without receiver readiness or a valid claimed identity was accepted.");
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
        {
        }
    }

    private Metadata Headers(string peer) => new()
    {
        { TransportProtocol.PeerHeader, peer },
        { TransportProtocol.LinkHeader, services.Ids.GenId() }
    };

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private sealed class WatcherStub : Watcher
    {
        public override Task process(WatchedEvent evt) => Task.CompletedTask;
    }
}
