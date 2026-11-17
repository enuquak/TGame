using Grpc.Core;
using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal static class MessageDispatchChecks
{
    public static async Task RunAsync(CancellationToken stop)
    {
        await SinglePointDispatchChecks.RunAsync(stop);
        await VerifyQueueLimitAsync(stop);
        Console.WriteLine("PASS direct SinglePoint dispatch and bounded request queues.");
    }

    private static async Task VerifyQueueLimitAsync(CancellationToken stop)
    {
        const int RequestCount = 1000;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new FrameStream { BeforeWrite = release.Task };

        using var node = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), TestNodes.DefaultGroup);
        stream.Input.Writer.TryWrite(TestNodes.Hello(ServerProcess.FormatName(nameof(ProcessType.Login), 1)));
        var running = node.RunLinkAsync(ServerProcess.FormatName(nameof(ProcessType.Login), 1), IdGenerator.GenId(), stream, stream, stop);
        var request = new RegisterEntitiesRequest { SinglePointId = IdGenerator.GenId() };
        request.EntityIds.Add(request.SinglePointId);
        // 阻塞注册回包，让后续请求填满接收队列。
        for (int i = 0; i < RequestCount; i++)
        {
            stream.Input.Writer.TryWrite(RpcFrames.Request(request, IdGenerator.GenId()));
        }

        try
        {
            await running.WaitAsync(stop);
            throw new Exception("A saturated request queue did not reject the stream.");
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.ResourceExhausted)
        {
        }
        finally
        {
            stream.Input.Writer.TryComplete();
            release.TrySetResult();
        }
    }
}
