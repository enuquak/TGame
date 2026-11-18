using Grpc.Core;
using TGame;
using TGame.Server.Engine;

internal static class StartupReadinessChecks
{
    internal static async Task RunAsync(CancellationToken stop)
    {
        await CheckPublicationWaitAsync(stop);
        await CheckInitializationWaitAsync(stop);
        await CheckDelayedHelloAsync(stop);
        Console.WriteLine("PASS current readiness wait, cancellation, recovery and delayed incoming handshake rejection.");
    }

    private static async Task CheckPublicationWaitAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeConfiguration(TestNodes.DefaultGroup);
        using var node = TestNodes.Create(nameof(ProcessType.Gate));
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
            await node.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
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

    private static async Task CheckInitializationWaitAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeIsolated(ProcessType.Router);
        using var node = TestNodes.Create(nameof(ProcessType.Router));
        var ready = node.WaitUntilReadyAsync(stop);
        Check(!ready.IsCompleted, "Readiness completed before SinglePoint initialization.");
        await node.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
        await ready.WaitAsync(stop);
        Check(node.Ready, "Initialization did not notify the pending readiness waiter.");
    }

    private static async Task CheckDelayedHelloAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeConfiguration(TestNodes.DefaultGroup);
        using var node = TestNodes.Create(nameof(ProcessType.Login));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var routerStream = new FrameStream();
        var router = Open(node, ProcessType.Router, routerStream, lifetime.Token);
        Task incoming = Task.CompletedTask;
        try
        {
            await node.FirstLinks.WaitAsync(stop);
            await node.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
            Check(node.Ready, "Login did not become ready before the incoming handshake.");

            var peer = ServerProcess.FormatName(nameof(ProcessType.Gate), 1);
            var stream = new FrameStream();
            incoming = node.RunLinkAsync(peer, IdGenerator.GenId(), stream, stream, lifetime.Token, requireReady: true);
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

    private static Task Open(ServerNode node, ProcessType type, FrameStream stream, CancellationToken stop)
    {
        var peer = ServerProcess.FormatName(type.ToString(), 1);
        stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
        return node.RunLinkAsync(peer, IdGenerator.GenId(), stream, stream, stop);
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
