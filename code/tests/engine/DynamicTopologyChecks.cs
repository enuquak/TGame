using TGame;
using TGame.Server.Engine;

internal sealed class DynamicTopologyChecks(TestServices services)
{
    public async Task RunAsync(CancellationToken stop)
    {
        var configuration = TestNodes.LoadConfiguration();
        var router = services.CreateNode(new ServerProcess(ProcessType.Router, 1), configuration);
        Check(router.FirstLinks.IsCompletedSuccessfully && router.Ready,
            "Router without outgoing dependencies must become ready without incoming processes.");

        var node = services.CreateNode(new ServerProcess(ProcessType.Base, 42), configuration);
        var streams = new Dictionary<string, FrameStream>();
        var tasks = new Dictionary<string, Task>();
        var router1 = ServerProcess.FormatName(nameof(ProcessType.Router), 1);
        var router2 = ServerProcess.FormatName(nameof(ProcessType.Router), 2);
        var router9 = ServerProcess.FormatName(nameof(ProcessType.Router), 9);
        var gate = ServerProcess.FormatName(nameof(ProcessType.Gate), 77);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            Open(router1);
            Check(!node.FirstLinks.IsCompleted && !node.Ready, "One Router must not replace the missing DBMgr type.");
            Open(ServerProcess.FormatName(nameof(ProcessType.DBMgr), 1));
            await node.FirstLinks.WaitAsync(stop);
            Check(node.Ready, "Base waited for incoming Gate connections.");
            Check(node.AcceptsIncoming(gate), "Incoming identities must not have a configured number ceiling.");
            Check(!node.AcceptsIncoming(ServerProcess.FormatName(nameof(ProcessType.Scene), 1)), "Unrelated incoming types must be rejected.");

            Open(router2);
            Check(node.Ready, "An extra Router revoked readiness.");
            await Close(router1);
            Check(node.Ready, "Losing a redundant Router revoked readiness.");
            await Close(router2);
            Check(!node.Ready && node.FirstLinks.IsCompletedSuccessfully,
                "Losing the last Router must revoke only current readiness.");

            var recovered = node.WaitUntilReadyAsync(stop);
            Open(router9);
            await recovered;
            Check(node.Ready, "A replacement Router did not restore readiness.");

            Open(gate);
            await streams[gate].Output.Reader.ReadAsync(stop);
            Check(node.Ready, "An incoming peer revoked readiness.");
            await Close(gate);
            Check(node.Ready, "Losing an incoming peer revoked readiness.");
            Console.WriteLine("PASS dynamic type readiness, redundant and incoming links, last-link loss and recovery.");
        }
        finally
        {
            lifetime.Cancel();
            foreach (var task in tasks.Values)
            {
                try
                {
                    await task;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }
        }

        void Open(string peer)
        {
            var stream = new FrameStream();
            stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
            streams[peer] = stream;
            tasks[peer] = node.RunLinkAsync(peer, services.Ids.GenId(), stream, stream, lifetime.Token);
        }

        async Task Close(string peer)
        {
            streams[peer].Input.Writer.Complete();
            await tasks[peer].WaitAsync(stop);
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
