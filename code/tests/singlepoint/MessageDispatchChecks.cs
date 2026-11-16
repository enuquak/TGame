using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame;
using TGame.Diagnostics;
using TGame.Management;
using TGame.Profile;
using TGame.Server.Engine;
using TGame.Server.Logic;
using TGame.Transport;

internal static class MessageDispatchChecks
{
    public static async Task RunAsync(CancellationToken stop)
    {
        await VerifyNestedDispatchAsync(stop);
        await VerifyDiagnosticRegistrationAsync(stop);
        await VerifyQueueLimitAsync(stop);
        Console.WriteLine("PASS nested RPC dispatch, optional diagnostic handlers and bounded request queues.");
    }

    private static async Task VerifyNestedDispatchAsync(CancellationToken stop)
    {
        var messages = new RpcDispatcher();
        var requestId = Guid.NewGuid().ToString("N");
        var entityId = Guid.NewGuid().ToString("N");
        var linkId = Guid.NewGuid().ToString("N");

        messages.Register<StringValue>(request => request.Value, async (peer, request, link, token) =>
        {
            if (request.Value != requestId || peer != ServerProcess.FormatName(nameof(ProcessType.Login), 1) || link.Id != linkId)
            {
                throw new Exception("Dispatch changed the payload or connection context.");
            }

            var nested = await link.GmPingAsync(new GmPingRequest { RequestId = requestId, EntityId = entityId }, token);
            var reply = new ProfileReply
            {
                RequestId = nested.RequestId,
                EntityId = nested.EntityId,
                Result = ProfileReply.Types.Result.Missing
            };
            await link.ReplyAsync(reply, token);
        });

        var stream = new FrameStream();
        using var node = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), TestNodes.DefaultGroup, messages);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        stream.Input.Writer.TryWrite(new Frame { Hello = ServerProcess.FormatName(nameof(ProcessType.Login), 1) });
        var running = node.RunLinkAsync(ServerProcess.FormatName(nameof(ProcessType.Login), 1), linkId, [], stream, stream, lifetime.Token);
        try
        {
            stream.Input.Writer.TryWrite(RpcFrames.Request(new StringValue { Value = requestId }, requestId));
            var frame = await stream.Output.Reader.ReadAsync(stop);
            var nested = frame.ReadRequest<GmPingRequest>();
            if (nested.RequestId != requestId || nested.EntityId != entityId)
            {
                throw new Exception("Registered request handler did not start its nested RPC.");
            }

            stream.Input.Writer.TryWrite(RpcFrames.Reply(
                new GmPingReply { RequestId = requestId, EntityId = entityId }, requestId));
            var responseFrame = await stream.Output.Reader.ReadAsync(stop);
            var response = responseFrame.ReadReply<ProfileReply>();
            if (response.RequestId != requestId || response.EntityId != entityId || response.Result != ProfileReply.Types.Result.Missing)
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

    private static async Task VerifyDiagnosticRegistrationAsync(CancellationToken stop)
    {
        var messages = new RpcDispatcher();
        var stream = new FrameStream();
        var link = new PeerLink(nameof(ProcessType.Gate), ServerProcess.FormatName(nameof(ProcessType.Login), 1), Guid.NewGuid().ToString("N"), stream);
        using var node = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), TestNodes.DefaultGroup, messages);
        ServerMessages.Register(messages, node);
        var ping = new Ping { RequestId = Guid.NewGuid().ToString("N"), EntityId = node.State.EntityId };
        try
        {
            await messages.DispatchAsync(RpcFrames.Request(ping).Request, ServerProcess.FormatName(nameof(ProcessType.Login), 1), link, stop);
            throw new Exception("Normal startup registered a diagnostic RPC handler.");
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented)
        {
        }

        var profile = new ProfileRequest { RequestId = Guid.NewGuid().ToString("N"), Fetch = node.State.EntityId };
        await messages.DispatchAsync(RpcFrames.Request(profile).Request, ServerProcess.FormatName(nameof(ProcessType.Login), 1), link, stop);
        var profileFrame = await stream.Output.Reader.ReadAsync(stop);
        if (profileFrame.ReadReply<ProfileReply>().Result != ProfileReply.Types.Result.Unavailable)
        {
            throw new Exception("Disabling diagnostics changed the Profile handler or its readiness check.");
        }

        var gm = new GmPingRequest { RequestId = Guid.NewGuid().ToString("N"), EntityId = node.State.EntityId };
        await messages.DispatchAsync(RpcFrames.Request(gm, gm.RequestId).Request, ServerProcess.FormatName(nameof(ProcessType.Router), 1), link, stop);
        var gmFrame = await stream.Output.Reader.ReadAsync(stop);
        if (gmFrame.ReadReply<GmPingReply>().Status != GmPingReply.Types.Status.Unavailable)
        {
            throw new Exception("Disabling diagnostics changed the GM handler or its readiness check.");
        }

        DiagnosticMessages.Register(messages, node);
        await messages.DispatchAsync(RpcFrames.Request(ping).Request, ServerProcess.FormatName(nameof(ProcessType.Login), 1), link, stop);
        var diagnosticFrame = await stream.Output.Reader.ReadAsync(stop);
        if (diagnosticFrame.ReadReply<Pong>().Status != Pong.Types.Status.Unavailable)
        {
            throw new Exception("Explicit diagnostic registration did not retain business readiness checks.");
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

        using var node = new GameServerNode(ServerProcess.FormatName(nameof(ProcessType.Gate), 1), TestNodes.DefaultGroup, messages);
        stream.Input.Writer.TryWrite(new Frame { Hello = ServerProcess.FormatName(nameof(ProcessType.Login), 1) });
        var running = node.RunLinkAsync(ServerProcess.FormatName(nameof(ProcessType.Login), 1), Guid.NewGuid().ToString("N"), [], stream, stream, stop);
        var id = Guid.NewGuid().ToString("N");
        stream.Input.Writer.TryWrite(RpcFrames.Request(new StringValue { Value = id }, id));
        await started.Task.WaitAsync(stop);
        for (int i = 0; i < RequestCount; i++)
        {
            id = Guid.NewGuid().ToString("N");
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
