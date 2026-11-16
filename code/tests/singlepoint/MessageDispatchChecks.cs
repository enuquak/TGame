using Google.Protobuf;
using Grpc.Core;
using System.Threading.Channels;
using TGame;
using TGame.Management;
using TGame.Profile;
using TGame.Server;
using TGame.Server.Logic;
using TGame.Transport;

internal static class MessageDispatchChecks
{
    public static async Task RunAsync(CancellationToken stop)
    {
        await VerifyNestedDispatchAsync(stop);
        VerifyRegistrationAndQueue();
    }

    private static async Task VerifyNestedDispatchAsync(CancellationToken stop)
    {
        var messages = new MessageDispatcher();
        var requestId = Guid.NewGuid().ToString("N");
        var entityId = Guid.NewGuid().ToString("N");
        var linkId = Guid.NewGuid().ToString("N");
        var payload = ByteString.CopyFromUtf8("custom payload, not a Ping");

        messages.RegisterRequest(Frame.BodyOneofCase.DiagnosticRequest, async (frame, context, token) =>
        {
            if (frame.DiagnosticRequest != payload || context.Peer != nameof(ProcessType.Login) ||
                context.LinkId != linkId || context.Node.Role != nameof(ProcessType.Gate))
            {
                throw new Exception("Dispatch changed the payload or connection context.");
            }

            var request = new GmPingRequest { RequestId = requestId, EntityId = entityId };
            var nested = await context.Link.GmPingAsync(request, token);
            var reply = new ProfileReply
            {
                RequestId = nested.RequestId,
                EntityId = nested.EntityId,
                Result = ProfileReply.Types.Result.Missing
            };
            await context.Link.ReplyAsync(reply, token);
        });

        GMMessages.Register(messages);
        var node = new ServerNode(nameof(ProcessType.Gate), new GateServerSinglePoint(), messages);
        var stream = new FrameStream();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        stream.Input.Writer.TryWrite(new Frame { Hello = nameof(ProcessType.Login) });
        var running = node.RunLinkAsync(nameof(ProcessType.Login), linkId, [], stream, stream, lifetime.Token);

        try
        {
            stream.Input.Writer.TryWrite(new Frame { DiagnosticRequest = payload });
            var nested = await stream.Output.Reader.ReadAsync(stop);
            if (nested.GmPingRequest?.RequestId != requestId || nested.GmPingRequest.EntityId != entityId)
            {
                throw new Exception("Registered request handler did not start its nested RPC.");
            }

            stream.Input.Writer.TryWrite(new Frame
            {
                GmPingReply = new GmPingReply { RequestId = requestId, EntityId = entityId }
            });

            var response = await stream.Output.Reader.ReadAsync(stop);
            if (response.ProfileReply?.RequestId != requestId || response.ProfileReply.EntityId != entityId ||
                response.ProfileReply.Result != ProfileReply.Types.Result.Missing)
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

    private static void VerifyRegistrationAndQueue()
    {
        var messages = new MessageDispatcher();
        var stream = new FrameStream();
        var link = new PeerLink(nameof(ProcessType.Gate), nameof(ProcessType.Login), Guid.NewGuid().ToString("N"), stream);
        var queue = Channel.CreateBounded<Frame>(1);
        var frame = new Frame { ProfileRequest = new ProfileRequest() };

        ExpectRpc(StatusCode.Unimplemented, () => messages.Receive(frame, link, queue.Writer));
        messages.RegisterRequest(Frame.BodyOneofCase.ProfileRequest, (_, _, _) => Task.CompletedTask);
        ExpectDuplicate(() => messages.RegisterRequest(Frame.BodyOneofCase.ProfileRequest, (_, _, _) => Task.CompletedTask));
        ExpectDuplicate(() => messages.RegisterReply(Frame.BodyOneofCase.ProfileRequest, IgnoreReply));

        messages.RegisterReply(Frame.BodyOneofCase.ProfileReply, IgnoreReply);
        ExpectDuplicate(() => messages.RegisterReply(Frame.BodyOneofCase.ProfileReply, IgnoreReply));
        ExpectDuplicate(() => messages.RegisterRequest(Frame.BodyOneofCase.ProfileReply, (_, _, _) => Task.CompletedTask));

        messages.Receive(frame, link, queue.Writer);
        ExpectRpc(StatusCode.ResourceExhausted, () => messages.Receive(frame, link, queue.Writer));
        ExpectRpc(StatusCode.Unimplemented, () => messages.Receive(new Frame(), link, queue.Writer));
        if (!queue.Reader.TryRead(out var queued) || queued != frame || queue.Reader.TryRead(out _))
        {
            throw new Exception("Rejected or duplicate frames entered the request queue.");
        }
    }

    private static void IgnoreReply(Frame frame, PeerLink link)
    {
    }

    private static void ExpectDuplicate(Action register)
    {
        try
        {
            register();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Exception("Duplicate registration overwrote an existing message handler.");
    }

    private static void ExpectRpc(StatusCode code, Action receive)
    {
        try
        {
            receive();
        }
        catch (RpcException error) when (error.StatusCode == code)
        {
            return;
        }

        throw new Exception($"Expected message rejection with {code}.");
    }
}
