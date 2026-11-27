using Grpc.Core;
using TGame;
using TGame.Server.Engine;

internal sealed class StartupReadinessChecks(TestServices services)
{
    internal async Task RunAsync(CancellationToken stop)
    {
        await CheckStartupMinimumAsync(stop);
        await CheckZeroMinimumAsync(stop);
        await CheckWithoutDependenciesAsync(stop);
        await CheckDelayedHelloAsync(stop);
        Console.WriteLine("PASS startup minimums, stable readiness after disconnect, cancellation and incoming handshakes.");
    }

    private async Task CheckStartupMinimumAsync(CancellationToken stop)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
        counts[ProcessType.Router] = 2;
        var startup = new ServerGroup(counts);
        var node = new ServerNode(new ServerProcess(ProcessType.Base, 1), TestNodes.LoadConfiguration(),
            startup, services.ZooKeeper, services.Ids, services.Log);
        var router1Stream = new FrameStream();
        var router2Stream = new FrameStream();
        var databaseStream = new FrameStream();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var router1 = Open(node, ProcessType.Router, router1Stream, lifetime.Token);
        var database = Open(node, ProcessType.DBMgr, databaseStream, lifetime.Token);
        var router2Name = ServerProcess.FormatName(nameof(ProcessType.Router), 2);
        var router2 = node.RunLinkAsync(router2Name, services.Ids.GenId(), router2Stream, router2Stream, lifetime.Token);
        Task recovered = Task.CompletedTask;
        try
        {
            Check(!node.Ready && !node.FirstLinks.IsCompleted,
                "Router=2 completed startup with only one Router; an unhandshaken connection must not count.");
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                var pending = node.FirstLinks.WaitAsync(cancelled.Token);
                cancelled.Cancel();
                try
                {
                    await pending;
                    throw new Exception("Startup minimum wait ignored cancellation.");
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
                {
                }
            }

            router2Stream.Input.Writer.TryWrite(TestNodes.Hello(router2Name));
            await node.FirstLinks.WaitAsync(stop);
            Check(node.Ready, "Two Routers and one DBMgr did not satisfy startup.");
            router1Stream.Input.Writer.Complete();
            await router1.WaitAsync(stop);
            Check(node.Ready, "The startup minimum was reapplied after one Router disconnected.");
            router2Stream.Input.Writer.Complete();
            await router2.WaitAsync(stop);
            Check(node.Ready && node.FirstLinks.IsCompletedSuccessfully,
                "Losing all Routers must preserve completed startup readiness.");
            databaseStream.Input.Writer.Complete();
            await database.WaitAsync(stop);
            Check(node.Ready, "Losing every dependency revoked completed startup readiness.");
            var ready = node.FirstLinks.WaitAsync(stop);
            Check(ready.IsCompletedSuccessfully, "Completed startup waited for disconnected peers again.");
            await ready;
            recovered = Open(node, ProcessType.Router, new FrameStream(), lifetime.Token);
            Check(node.Ready, "Reconnection incorrectly required the startup minimum again.");
        }
        finally
        {
            lifetime.Cancel();
            await StopAsync(router1, database, router2, recovered);
        }
    }

    private async Task CheckZeroMinimumAsync(CancellationToken stop)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
        counts[ProcessType.Router] = 0;
        var node = new ServerNode(new ServerProcess(ProcessType.Login, 1), TestNodes.LoadConfiguration(),
            new ServerGroup(counts), services.ZooKeeper, services.Ids, services.Log);
        Check(node.Ready && node.FirstLinks.IsCompletedSuccessfully, "A zero minimum blocked startup.");
        var stream = new FrameStream();
        var connection = Open(node, ProcessType.Router, stream, stop);
        try
        {
            Check(node.Ready, "A late peer with a zero startup minimum revoked readiness.");
        }
        finally
        {
            stream.Input.Writer.Complete();
            await connection.WaitAsync(stop);
        }

        Check(node.Ready, "Losing an optional startup type revoked readiness.");
    }

    internal async Task CheckWithoutDependenciesAsync(CancellationToken stop)
    {
        var configuration = TestNodes.LoadConfiguration();
        var node = services.CreateNode(new ServerProcess(ProcessType.Router, 1), configuration);
        Check(node.Ready, "Router without outgoing dependencies must be ready without business initialization.");
        Check(node.FirstLinks.IsCompletedSuccessfully, "Router waited for incoming connections.");
        await node.FirstLinks.WaitAsync(stop);
        Console.WriteLine("PASS Router readiness requires no business initialization.");
    }

    private async Task CheckDelayedHelloAsync(CancellationToken stop)
    {
        var configuration = TestNodes.LoadConfiguration();
        var node = services.CreateNode(new ServerProcess(ProcessType.Login, 1), configuration);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var peer = ServerProcess.FormatName(nameof(ProcessType.Gate), 1);
        var unreadyStream = new FrameStream();
        unreadyStream.Input.Writer.TryWrite(TestNodes.Hello(peer));
        try
        {
            await node.AcceptConnectAsync(peer, services.Ids.GenId(), unreadyStream, unreadyStream, stop);
            throw new Exception("An initializing receiver accepted an incoming Hello.");
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
        {
        }

        Check(!unreadyStream.Output.Reader.TryPeek(out _), "An initializing receiver acknowledged an incoming Hello.");
        var routerStream = new FrameStream();
        var router = Open(node, ProcessType.Router, routerStream, lifetime.Token);
        Task incoming = Task.CompletedTask;
        try
        {
            await node.FirstLinks.WaitAsync(stop);
            Check(node.Ready, "Login did not become ready before the incoming handshake.");

            var stream = new FrameStream();
            incoming = node.RunLinkAsync(peer, services.Ids.GenId(), stream, stream, lifetime.Token, replyToHello: true);
            routerStream.Input.Writer.Complete();
            await router.WaitAsync(stop);
            Check(node.Ready, "Losing Router revoked Login startup readiness.");

            stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
            var hello = await stream.Output.Reader.ReadAsync(stop);
            Check(hello.Hello == node.Name, "Dependency loss rejected a delayed incoming Hello.");
            stream.Input.Writer.Complete();
            await incoming.WaitAsync(stop);

            var nextStream = new FrameStream();
            nextStream.Input.Writer.TryWrite(TestNodes.Hello(peer));
            incoming = node.RunLinkAsync(peer, services.Ids.GenId(), nextStream, nextStream, lifetime.Token, replyToHello: true);
            var nextHello = await nextStream.Output.Reader.ReadAsync(stop);
            Check(nextHello.Hello == node.Name, "A new incoming connection was rejected after all dependencies disconnected.");
        }
        finally
        {
            lifetime.Cancel();
            await StopAsync(router, incoming);
        }
    }

    private Task Open(ServerNode node, ProcessType type, FrameStream stream, CancellationToken stop)
    {
        var peer = ServerProcess.FormatName(type.ToString(), 1);
        stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
        return node.RunLinkAsync(peer, services.Ids.GenId(), stream, stream, stop);
    }

    private static async Task StopAsync(params Task[] connections)
    {
        foreach (var connection in connections)
        {
            try
            {
                await connection;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
