using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal static class RpcChecks
{
    public static void CheckTransportBoundary()
    {
        var engine = typeof(ServerNode).Assembly;
        var business = typeof(TGame.Profile.ProfileRequest).Assembly;
        if (engine.GetReferencedAssemblies().Any(reference => reference.Name == business.GetName().Name))
        {
            throw new Exception("Engine must not reference business Shared.");
        }

        if (business.GetReferencedAssemblies().Any(reference => reference.Name == engine.GetName().Name))
        {
            throw new Exception("Business Shared must not reference the server engine.");
        }

        System.Type[] engineTypes = [typeof(Frame), typeof(ServerLink), typeof(ProcessType), typeof(ServerGroup),
            typeof(ServerProcess), typeof(IdText), typeof(ServerRunFiles), typeof(ServerLogEvents),
            typeof(ServerLogFields), typeof(TGame.Logging.LogEventJson)];
        foreach (var type in engineTypes)
        {
            if (type.Assembly != engine)
            {
                throw new Exception($"{type.Name} must belong to the engine assembly.");
            }
        }

        foreach (var dependency in Frame.Descriptor.File.Dependencies)
        {
            if (dependency.Package.StartsWith("tgame.", StringComparison.Ordinal))
            {
                throw new Exception($"Transport must not depend on application protocol {dependency.Name}.");
            }
        }
    }

    public static async Task RunAsync(CancellationToken stop)
    {
        await CheckGenericDispatchAsync(stop);
        await CheckReplyCorrelationAsync(stop);
        await CheckInvalidMessagesAsync(stop);
        await CheckCancellationAndCloseAsync(stop);
        Console.WriteLine("PASS generic RPC registration, correlation, invalid messages, cancellation and disconnect.");
    }

    private static async Task CheckGenericDispatchAsync(CancellationToken stop)
    {
        var callerStream = new FrameStream();
        var receiverStream = new FrameStream();
        var caller = CreateLink(callerStream);
        var receiver = CreateLink(receiverStream);
        var dispatcher = new RpcDispatcher();
        dispatcher.Register<StringValue>(request => request.Value, async (peer, request, link, token) =>
        {
            var response = new BytesValue { Value = ByteString.CopyFromUtf8(peer + request.Value) };
            await link.ReplyAsync(response, request.Value, token);
        });

        var requestId = TestIds.Generator.GenId();
        var pending = caller.CallAsync<BytesValue>(new StringValue { Value = requestId }, requestId, stop);
        var requestFrame = await callerStream.Output.Reader.ReadAsync(stop);
        var request = Frame.Parser.ParseFrom(requestFrame.ToByteArray()).Request;
        await dispatcher.DispatchAsync(request, nameof(ProcessType.Login), receiver, stop);

        var replyFrame = await receiverStream.Output.Reader.ReadAsync(stop);
        var reply = Frame.Parser.ParseFrom(replyFrame.ToByteArray()).Reply;
        if (reply.RequestId != requestId)
        {
            throw new Exception("RPC reply lost its envelope request ID.");
        }

        caller.Receive(reply);
        var result = await pending;
        if (result.Value.ToStringUtf8() != nameof(ProcessType.Login) + requestId)
        {
            throw new Exception("An ordinary protobuf RPC did not reach its registered handler and return its reply.");
        }

        await ThrowsAsync<InvalidOperationException>(() =>
        {
            dispatcher.Register<StringValue>(value => value.Value, (_, _, _, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
    }

    private static async Task CheckReplyCorrelationAsync(CancellationToken stop)
    {
        var stream = new FrameStream();
        var link = CreateLink(stream);
        var firstId = TestIds.Generator.GenId();
        var secondId = TestIds.Generator.GenId();
        var first = link.CallAsync<StringValue>(new Empty(), firstId, stop);
        var second = link.CallAsync<Int32Value>(new Empty(), secondId, stop);
        await stream.Output.Reader.ReadAsync(stop);
        await stream.Output.Reader.ReadAsync(stop);

        await ThrowsAsync<InvalidDataException>(() => link.CallAsync<StringValue>(new Empty(), firstId, stop));

        link.Receive(RpcFrames.Reply(new Empty(), TestIds.Generator.GenId()).Reply);
        if (first.IsCompleted || second.IsCompleted)
        {
            throw new Exception("An unmatched reply completed another request.");
        }

        link.Receive(RpcFrames.Reply(new Int32Value { Value = 42 }, secondId).Reply);
        if ((await second).Value != 42 || first.IsCompleted)
        {
            throw new Exception("Out-of-order RPC replies were delivered to the wrong caller.");
        }

        link.Receive(RpcFrames.Reply(new StringValue { Value = firstId }, firstId).Reply);
        if ((await first).Value != firstId)
        {
            throw new Exception("The first RPC lost its reply.");
        }

        var wrongType = link.CallAsync<StringValue>(new Empty(), firstId, stop);
        await stream.Output.Reader.ReadAsync(stop);
        link.Receive(RpcFrames.Reply(new Empty(), firstId).Reply);
        await ThrowsAsync<InvalidDataException>(() => wrongType);
    }

    private static async Task CheckInvalidMessagesAsync(CancellationToken stop)
    {
        var stream = new FrameStream();
        var link = CreateLink(stream);
        var dispatcher = new RpcDispatcher();
        bool invoked = false;
        dispatcher.Register<StringValue>(request => request.Value, (_, _, _, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        var requestId = TestIds.Generator.GenId();
        var mismatched = RpcFrames.Request(new StringValue { Value = TestIds.Generator.GenId() }, requestId).Request;
        await ThrowsAsync<InvalidDataException>(() => Dispatch(mismatched));
        await ThrowsAsync<InvalidDataException>(() => Dispatch(new RpcRequest { RequestId = requestId }));
        await ThrowsAsync<InvalidDataException>(() => Dispatch(new RpcRequest { Payload = Any.Pack(new Empty()) }));
        await ThrowsAsync<InvalidDataException>(() => Dispatch(new RpcRequest { RequestId = requestId, Payload = new Any() }));

        var unknown = RpcFrames.Request(new Empty(), requestId).Request;
        try
        {
            await Dispatch(unknown);
            throw new Exception("An unregistered request type was accepted.");
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented)
        {
        }

        var malformed = RpcFrames.Request(new StringValue { Value = requestId }, requestId).Request;
        malformed.Payload.Value = ByteString.CopyFrom(new byte[] { 0x0a, 0xff });
        await ThrowsAsync<InvalidProtocolBufferException>(() => Dispatch(malformed));
        if (invoked)
        {
            throw new Exception("An invalid request reached its RPC handler.");
        }

        await ThrowsAsync<InvalidDataException>(() =>
        {
            link.Receive(new RpcReply { RequestId = requestId });
            return Task.CompletedTask;
        });

        Task Dispatch(RpcRequest request) => dispatcher.DispatchAsync(request, nameof(ProcessType.Login), link, stop);
    }

    private static async Task CheckCancellationAndCloseAsync(CancellationToken stop)
    {
        var stream = new FrameStream();
        var link = CreateLink(stream);
        var requestId = TestIds.Generator.GenId();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var pending = link.CallAsync<Empty>(new Empty(), requestId, cancelled.Token);
        await stream.Output.Reader.ReadAsync(stop);

        cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => pending);
        link.Receive(RpcFrames.Reply(new Empty(), requestId).Reply);

        var next = link.CallAsync<Empty>(new Empty(), requestId, stop);
        await stream.Output.Reader.ReadAsync(stop);
        link.Close();
        await ThrowsAsync<IOException>(() => next);
        await ThrowsAsync<IOException>(() => link.CallAsync<Empty>(new Empty(), TestIds.Generator.GenId(), stop));
    }

    private static PeerLink CreateLink(FrameStream stream) =>
        new(nameof(ProcessType.Login), nameof(ProcessType.Router), TestIds.Generator.GenId(), stream);

    private static async Task ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected {typeof(TException).Name}.");
    }
}
