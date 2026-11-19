using Grpc.Core;
using TGame;
using TGame.Server.Engine;

internal sealed class StartupReadinessChecks(TestServices services)
{
    internal async Task RunAsync(CancellationToken stop)
    {
        await CheckStartupMinimumAsync(stop);
        await CheckZeroMinimumAsync(stop);
        await CheckPublicationWaitAsync(stop);
        await CheckWithoutDependenciesAsync(stop);
        await CheckDelayedHelloAsync(stop);
        Console.WriteLine("PASS startup minimums, zero minimum, current readiness, cancellation and delayed handshake rejection.");
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
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var router1 = Open(node, ProcessType.Router, router1Stream, lifetime.Token);
        var database = Open(node, ProcessType.DBMgr, new FrameStream(), lifetime.Token);
        var router2Name = ServerProcess.FormatName(nameof(ProcessType.Router), 2);
        var router2 = node.RunLinkAsync(router2Name, services.Ids.GenId(), router2Stream, router2Stream, lifetime.Token);
        Task recovered = Task.CompletedTask;
        try
        {
            Check(!node.Ready && !node.FirstLinks.IsCompleted,
                "Router=2 completed startup with only one Router; an unhandshaken connection must not count.");
            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                var pending = node.WaitUntilReadyAsync(cancelled.Token);
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
            await node.WaitUntilReadyAsync(stop);
            Check(node.Ready, "Two Routers and one DBMgr did not satisfy startup.");
            router1Stream.Input.Writer.Complete();
            await router1.WaitAsync(stop);
            Check(node.Ready, "The startup minimum was reapplied after one Router disconnected.");
            router2Stream.Input.Writer.Complete();
            await router2.WaitAsync(stop);
            Check(!node.Ready && node.FirstLinks.IsCompletedSuccessfully,
                "Losing all Routers must report disconnection without erasing completed startup.");
            recovered = Open(node, ProcessType.Router, new FrameStream(), lifetime.Token);
            await node.WaitUntilReadyAsync(stop);
            Check(node.Ready, "Recovery incorrectly required two Routers again.");
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

    private async Task CheckPublicationWaitAsync(CancellationToken stop)
    {
        var configuration = TestNodes.LoadConfiguration();
        var node = services.CreateNode(new ServerProcess(ProcessType.Gate, 1), configuration);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var loginStream = new FrameStream();
        var baseStream = new FrameStream();
        var login = Open(node, ProcessType.Login, loginStream, lifetime.Token);
        var baseLink = Open(node, ProcessType.Base, baseStream, lifetime.Token);
        try
        {
            await node.FirstLinks.WaitAsync(stop);
            loginStream.Input.Writer.Complete();
            await login.WaitAsync(stop);
            Check(!node.Ready, "The missing Login dependency did not revoke readiness.");
            var ready = node.WaitUntilReadyAsync(lifetime.Token);
            Check(!ready.IsCompleted, "Initial publication reused the first-link milestone after dependency loss.");

            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                var pending = node.WaitUntilReadyAsync(cancelled.Token);
                cancelled.Cancel();
                try
                {
                    await pending;
                    throw new Exception("Waiting for current readiness ignored cancellation.");
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
                {
                }
            }

            login = Open(node, ProcessType.Login, new FrameStream(), lifetime.Token);
            await ready.WaitAsync(stop);
            Check(node.Ready, "Readiness wait completed without dependency recovery.");
        }
        finally
        {
            lifetime.Cancel();
            await StopAsync(login, baseLink);
        }
    }

    internal async Task CheckWithoutDependenciesAsync(CancellationToken stop)
    {
        var configuration = TestNodes.LoadConfiguration();
        var node = services.CreateNode(new ServerProcess(ProcessType.Router, 1), configuration);
        Check(node.Ready, "Router without outgoing dependencies must be ready without business initialization.");
        Check(node.FirstLinks.IsCompletedSuccessfully, "Router waited for incoming connections.");
        await node.WaitUntilReadyAsync(stop);
        Console.WriteLine("PASS Router readiness requires no business initialization.");
    }

    private async Task CheckDelayedHelloAsync(CancellationToken stop)
    {
        var configuration = TestNodes.LoadConfiguration();
        var node = services.CreateNode(new ServerProcess(ProcessType.Login, 1), configuration);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var routerStream = new FrameStream();
        var router = Open(node, ProcessType.Router, routerStream, lifetime.Token);
        Task incoming = Task.CompletedTask;
        try
        {
            await node.FirstLinks.WaitAsync(stop);
            Check(node.Ready, "Login did not become ready before the incoming handshake.");

            var peer = ServerProcess.FormatName(nameof(ProcessType.Gate), 1);
            var stream = new FrameStream();
            incoming = node.RunLinkAsync(peer, services.Ids.GenId(), stream, stream, lifetime.Token, requireReady: true);
            routerStream.Input.Writer.Complete();
            await router.WaitAsync(stop);
            Check(!node.Ready, "Losing Router did not revoke Login readiness.");

            stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
            var hello = stream.Output.Reader.ReadAsync(lifetime.Token).AsTask();
            Check(await Task.WhenAny(incoming, hello) == incoming, "An unready receiver acknowledged a delayed Hello.");
            try
            {
                await incoming;
                throw new Exception("An unready receiver accepted a delayed Hello.");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unavailable)
            {
                incoming = Task.CompletedTask;
            }
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
