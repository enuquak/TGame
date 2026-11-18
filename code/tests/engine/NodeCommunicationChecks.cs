using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal static class NodeCommunicationChecks
{
    internal static async Task RunAsync(CancellationToken stop)
    {
        await CheckPeerCallsAsync(stop);
        await CheckIncomingRequestsAsync(stop);
        await CheckRejectedHandshakeAsync(stop);
        Console.WriteLine("PASS direct peer RPC payloads, errors, cancellation, disconnect and unsupported requests.");
    }

    private static async Task CheckPeerCallsAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeConfiguration(TestNodes.DefaultGroup);
        var node = TestNodes.Create(nameof(ProcessType.Login));
        var peer = ServerProcess.FormatName(nameof(ProcessType.Router), 1);
        var stream = new FrameStream();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
        var running = node.RunLinkAsync(peer, IdGenerator.GenId(), stream, stream, lifetime.Token);
        try
        {
            await node.WaitUntilReadyAsync(stop);
            var request = new StringValue { Value = "payload" };
            var call = node.CallPeerAsync<StringValue>(peer, request, stop);
            var frame = await stream.Output.Reader.ReadAsync(stop);
            Check(IdText.IsValid(frame.Request.RequestId) && frame.ReadRequest<StringValue>().Equals(request),
                "Direct RPC lost its request ID or changed its payload.");
            stream.Input.Writer.TryWrite(RpcFrames.Reply(new StringValue { Value = "reply" }, frame.Request.RequestId));
            Check((await call).Value == "reply", "The peer reply did not reach its caller.");

            var failed = node.CallPeerAsync<Empty>(peer, new Empty(), stop);
            var failedFrame = await stream.Output.Reader.ReadAsync(stop);
            stream.Input.Writer.TryWrite(new Frame
            {
                Reply = new RpcReply
                {
                    RequestId = failedFrame.Request.RequestId,
                    Error = new RpcError { StatusCode = (int)StatusCode.Unimplemented, Detail = "No handler." }
                }
            });
            await ExpectStatusAsync(StatusCode.Unimplemented, () => failed);

            var missing = ServerProcess.FormatName(nameof(ProcessType.Profile), 1);
            await ExpectStatusAsync(StatusCode.Unavailable, () => node.CallPeerAsync<Empty>(missing, new Empty(), stop));
            Check(!stream.Output.Reader.TryPeek(out _), "A missing direct peer was routed through another process.");

            using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                cancelled.Cancel();
                try
                {
                    await node.CallPeerAsync<Empty>(peer, new Empty(), cancelled.Token);
                    throw new Exception("Direct RPC ignored caller cancellation.");
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
                {
                }
            }

            var disconnected = node.CallPeerAsync<Empty>(peer, new Empty(), stop);
            await stream.Output.Reader.ReadAsync(stop);
            stream.Input.Writer.Complete();
            await running.WaitAsync(stop);
            try
            {
                await disconnected;
                throw new Exception("Disconnect did not fail the pending direct RPC.");
            }
            catch (IOException)
            {
            }

            Check(!node.Ready, "A disconnected dependency left the node ready.");
            await ExpectStatusAsync(StatusCode.Unavailable, () => node.CallPeerAsync<Empty>(peer, new Empty(), stop));
        }
        finally
        {
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

    private static async Task CheckIncomingRequestsAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeConfiguration(TestNodes.DefaultGroup);
        var node = TestNodes.Create(nameof(ProcessType.Router));
        var peer = ServerProcess.FormatName(nameof(ProcessType.Scene), 1);
        var stream = new FrameStream();
        stream.Input.Writer.TryWrite(TestNodes.Hello(peer));
        var running = node.RunLinkAsync(peer, IdGenerator.GenId(), stream, stream, stop, requireReady: true);
        try
        {
            var hello = await stream.Output.Reader.ReadAsync(stop);
            Check(hello.Hello == node.Name, "The receiver did not acknowledge the process identity.");

            for (var index = 0; index < 2; index++)
            {
                var requestId = IdGenerator.GenId();
                stream.Input.Writer.TryWrite(RpcFrames.Request(new Empty(), requestId));
                var response = await stream.Output.Reader.ReadAsync(stop);
                Check(response.Reply?.RequestId == requestId &&
                    response.Reply.Error?.StatusCode == (int)StatusCode.Unimplemented,
                    "A valid request without a handler must return a correlated Unimplemented error.");
            }

            stream.Input.Writer.TryWrite(new Frame { Request = new RpcRequest { Payload = Any.Pack(new Empty()) } });
            try
            {
                await running.WaitAsync(stop);
                throw new Exception("A malformed request envelope did not terminate its connection.");
            }
            catch (InvalidDataException)
            {
            }
        }
        finally
        {
            stream.Input.Writer.TryComplete();
        }
    }

    private static async Task CheckRejectedHandshakeAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeConfiguration(TestNodes.DefaultGroup);
        var node = TestNodes.Create(nameof(ProcessType.Login));
        var peer = ServerProcess.FormatName(nameof(ProcessType.Router), 1);
        var stream = new FrameStream();
        stream.Input.Writer.TryWrite(TestNodes.Hello(ServerProcess.FormatName(nameof(ProcessType.Router), 2)));
        try
        {
            await node.RunLinkAsync(peer, IdGenerator.GenId(), stream, stream, stop);
            throw new Exception("A mismatched process identity completed its handshake.");
        }
        catch (IOException)
        {
        }

        Check(!node.Ready && !node.FirstLinks.IsCompleted, "A failed handshake satisfied an outgoing dependency.");
    }

    private static async Task ExpectStatusAsync(StatusCode expected, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (RpcException error) when (error.StatusCode == expected)
        {
            return;
        }

        throw new Exception($"Expected RPC status {expected}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}
