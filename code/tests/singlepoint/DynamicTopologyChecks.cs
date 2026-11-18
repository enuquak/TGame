using TGame;
using TGame.Server.Engine;
using TGame.Transport;
using Empty = Google.Protobuf.WellKnownTypes.Empty;

internal static class DynamicTopologyChecks
{
    public static async Task RunAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeConfiguration(TestNodes.DefaultGroup);
        using var router = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Router), 1));
        if (!router.FirstLinks.IsCompletedSuccessfully)
        {
            throw new Exception("Router must finish the connection stage without waiting for incoming processes.");
        }

        await router.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
        if (!router.Ready)
        {
            throw new Exception("Router with no outgoing dependencies must become ready.");
        }

        using var node = new ServerNode("Base42");
        var streams = new Dictionary<string, FrameStream>();
        var tasks = new Dictionary<string, Task>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            Open("Router1");
            Check(!node.FirstLinks.IsCompleted, "One Router must not replace the missing DBMgr type.");
            Open("DBMgr1");
            await node.FirstLinks.WaitAsync(stop);
            Check(node.AcceptsIncoming("Gate77"), "Incoming identities must not have a configured number ceiling.");
            Check(!node.AcceptsIncoming("Scene1"), "Unrelated incoming types must be rejected.");

            // 额外连接不确认注册，不能挡住已有的每类型一个成功连接。
            Open("Router2", acknowledge: false);
            await node.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
            Check(node.Ready, "Base waited for a Gate or a redundant Router registration.");
            var pending = await streams["Router2"].Output.Reader.ReadAsync(stop);
            var calls = Enumerable.Range(0, 32).Select(_ =>
                node.CallSinglePointAsync<Empty>(ProcessType.Scene, new Empty(), stop)).ToArray();
            if (streams["Router2"].Output.Reader.TryPeek(out _))
            {
                throw new Exception("Business traffic used a Router whose registration was not acknowledged.");
            }

            foreach (var call in calls)
            {
                var forwarded = await streams["Router1"].Output.Reader.ReadAsync(stop);
                streams["Router1"].Input.Writer.TryWrite(RpcFrames.Reply(new Empty(), forwarded.Request.RequestId));
            }

            await Task.WhenAll(calls);
            streams["Router2"].Input.Writer.TryWrite(RpcFrames.Reply(new RegisterEntitiesReply(), pending.Request.RequestId));
            await Close("Router1");
            while (!node.Ready)
            {
                await Task.Delay(1, stop);
            }

            await Close("Router2");
            Check(!node.Ready && node.FirstLinks.IsCompletedSuccessfully, "Losing the last Router must revoke only current readiness.");
            var point = node.SinglePoint;
            Open("Router9");
            while (!node.Ready)
            {
                await Task.Delay(1, stop);
            }

            Check(ReferenceEquals(point, node.SinglePoint), "Recovery recreated the SinglePoint.");
            Open("Gate77", acknowledge: false);
            await streams["Gate77"].Output.Reader.ReadAsync(stop);
            Check(node.Ready, "An incoming peer without registration acknowledgement revoked readiness.");
            await Close("Gate77");
            Check(node.Ready, "Losing an incoming peer revoked readiness.");
            Console.WriteLine("PASS dynamic type readiness, redundant and incoming links, registration threshold, last-link loss and recovery.");
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

        void Open(string peer, bool acknowledge = true)
        {
            var stream = new FrameStream { AcknowledgeRegistration = acknowledge };
            stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
            streams[peer] = stream;
            tasks[peer] = node.RunLinkAsync(peer, IdGenerator.GenId(), stream, stream, lifetime.Token);
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
