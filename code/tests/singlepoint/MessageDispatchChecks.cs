using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame;
using TGame.Profile;
using TGame.Server.Engine;
using TGame.Server.Logic;
using TGame.Transport;

internal static class MessageDispatchChecks
{
    public static async Task RunAsync(CancellationToken stop)
    {
        await VerifyNestedDispatchAsync(stop);
        await SinglePointDispatchChecks.RunAsync(stop);
        await VerifyQueueLimitAsync(stop);
        Console.WriteLine("PASS nested RPC dispatch, direct SinglePoint dispatch and bounded request queues.");
    }

    private static async Task VerifyNestedDispatchAsync(CancellationToken stop)
    {
        var messages = new RpcDispatcher();
        var requestId = IdGenerator.GenId();
        var linkId = IdGenerator.GenId();

        messages.Register<StringValue>(request => request.Value, async (peer, request, link, token) =>
        {
            if (request.Value != requestId || peer != ServerProcess.FormatName(nameof(ProcessType.Login), 1) || link.Id != linkId)
            {
                throw new Exception("Dispatch changed the payload or connection context.");
            }

            var nested = await link.CallAsync<Int32Value>(request, request.Value, token);
            await link.ReplyAsync(new Int32Value { Value = nested.Value + 1 }, request.Value, token);
        });

        var stream = new FrameStream();
        using var node = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), TestNodes.DefaultGroup, messages);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        stream.Input.Writer.TryWrite(TestNodes.Hello(ServerProcess.FormatName(nameof(ProcessType.Login), 1)));
        var running = node.RunLinkAsync(ServerProcess.FormatName(nameof(ProcessType.Login), 1), linkId, stream, stream, lifetime.Token);
        try
        {
            stream.Input.Writer.TryWrite(RpcFrames.Request(new StringValue { Value = requestId }, requestId));
            var frame = await stream.Output.Reader.ReadAsync(stop);
            var nested = frame.ReadRequest<StringValue>();
            if (frame.Request.RequestId != requestId || nested.Value != requestId)
            {
                throw new Exception("Registered request handler did not start its nested RPC.");
            }

            stream.Input.Writer.TryWrite(RpcFrames.Reply(
                new Int32Value { Value = 41 }, requestId));
            var responseFrame = await stream.Output.Reader.ReadAsync(stop);
            var response = responseFrame.ReadReply<Int32Value>();
            if (responseFrame.Reply.RequestId != requestId || response.Value != 42)
            {
                throw new Exception("Nested reply was blocked by request dispatch or matched incorrectly.");
            }
        }
        finally
        {
            stream.Input.Writer.TryComplete();
            lifetime.Cancel();
            try
            {
                await running;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task VerifyQueueLimitAsync(CancellationToken stop)
    {
        const int RequestCount = 1000;
        var messages = new RpcDispatcher();
        var stream = new FrameStream();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        messages.Register<StringValue>(request => request.Value, (_, _, _, token) =>
        {
            started.TrySetResult();
            return release.Task.WaitAsync(token);
        });

        using var node = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), TestNodes.DefaultGroup, messages);
        stream.Input.Writer.TryWrite(TestNodes.Hello(ServerProcess.FormatName(nameof(ProcessType.Login), 1)));
        var running = node.RunLinkAsync(ServerProcess.FormatName(nameof(ProcessType.Login), 1), IdGenerator.GenId(), stream, stream, stop);
        var id = IdGenerator.GenId();
        stream.Input.Writer.TryWrite(RpcFrames.Request(new StringValue { Value = id }, id));
        await started.Task.WaitAsync(stop);
        for (int i = 0; i < RequestCount; i++)
        {
            id = IdGenerator.GenId();
            stream.Input.Writer.TryWrite(RpcFrames.Request(new StringValue { Value = id }, id));
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
