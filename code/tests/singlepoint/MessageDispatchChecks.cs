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
        await VerifyProfileReadinessAsync(stop);
        await VerifyQueueLimitAsync(stop);
        Console.WriteLine("PASS nested RPC dispatch, Profile readiness and bounded request queues.");
    }

    private static async Task VerifyNestedDispatchAsync(CancellationToken stop)
    {
        var messages = new RpcDispatcher();
        var requestId = TestIds.Generator.GenId().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var linkId = TestIds.Generator.GenId().ToString(System.Globalization.CultureInfo.InvariantCulture);

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
        using var node = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), TestNodes.DefaultGroup, messages, TestIds.Generator);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        stream.Input.Writer.TryWrite(TestNodes.Hello(ServerProcess.FormatName(nameof(ProcessType.Login), 1)));
        var running = node.RunLinkAsync(ServerProcess.FormatName(nameof(ProcessType.Login), 1), linkId, [], stream, stream, lifetime.Token);
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

    private static async Task VerifyProfileReadinessAsync(CancellationToken stop)
    {
        var messages = new RpcDispatcher();
        using var node = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), TestNodes.DefaultGroup, messages, TestIds.Generator);
        ServerMessages.Register(messages, node);
        var profile = new ProfileRequest { RequestId = TestIds.Generator.GenId().ToString(System.Globalization.CultureInfo.InvariantCulture), Fetch = node.State.EntityId };
        var reply = (ProfileReply)await messages.DispatchEntityAsync(RpcFrames.Request(profile).Request, stop);
        if (reply.Result != ProfileReply.Types.Result.Unavailable)
        {
            throw new Exception("Profile accepted a request before the node was ready.");
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

        using var node = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), TestNodes.DefaultGroup, messages, TestIds.Generator);
        stream.Input.Writer.TryWrite(TestNodes.Hello(ServerProcess.FormatName(nameof(ProcessType.Login), 1)));
        var running = node.RunLinkAsync(ServerProcess.FormatName(nameof(ProcessType.Login), 1), TestIds.Generator.GenId().ToString(System.Globalization.CultureInfo.InvariantCulture), [], stream, stream, stop);
        var id = TestIds.Generator.GenId().ToString(System.Globalization.CultureInfo.InvariantCulture);
        stream.Input.Writer.TryWrite(RpcFrames.Request(new StringValue { Value = id }, id));
        await started.Task.WaitAsync(stop);
        for (int i = 0; i < RequestCount; i++)
        {
            id = TestIds.Generator.GenId().ToString(System.Globalization.CultureInfo.InvariantCulture);
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
