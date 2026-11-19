using Google.Protobuf.WellKnownTypes;
using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal sealed class RpcChecks(TestServices services)
{
    public void CheckTransportBoundary()
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

        System.Type[] engineTypes = [typeof(Frame), typeof(ServerLink), typeof(ProcessType), typeof(ServerGroup), typeof(ServerConfiguration),
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

    public async Task RunAsync(CancellationToken stop)
    {
        await CheckReplyCorrelationAsync(stop);
        await CheckInvalidMessagesAsync(stop);
        await CheckCancellationAndCloseAsync(stop);
        await CheckTimeoutAsync(stop);
        await CheckBlockedWriteTimeoutAsync(stop);
        Console.WriteLine("PASS RPC correlation, invalid envelopes, cancellation, timeout and disconnect.");
    }

    private async Task CheckReplyCorrelationAsync(CancellationToken stop)
    {
        var stream = new FrameStream();
        var link = CreateLink(stream);
        var firstId = services.Ids.GenId();
        var secondId = services.Ids.GenId();
        var first = link.CallAsync<StringValue>(new Empty(), firstId, stop);
        var second = link.CallAsync<Int32Value>(new Empty(), secondId, stop);
        await stream.Output.Reader.ReadAsync(stop);
        await stream.Output.Reader.ReadAsync(stop);

        await ThrowsAsync<InvalidDataException>(() => link.CallAsync<StringValue>(new Empty(), firstId, stop));

        link.Receive(RpcFrames.Reply(new Empty(), services.Ids.GenId()).Reply);
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

    private async Task CheckInvalidMessagesAsync(CancellationToken stop)
    {
        var stream = new FrameStream();
        var link = CreateLink(stream);
        var requestId = services.Ids.GenId();
        RpcRequest[] invalidRequests =
        [
            new() { RequestId = requestId },
            new() { Payload = Any.Pack(new Empty()) },
            new() { RequestId = requestId, Payload = new Any() }
        ];
        foreach (var request in invalidRequests)
        {
            await ThrowsAsync<InvalidDataException>(() => link.CallAsync(request, stop));
            await ThrowsAsync<InvalidDataException>(() =>
            {
                link.Receive(new RpcReply { RequestId = request.RequestId, Payload = request.Payload });
                return Task.CompletedTask;
            });
        }

        if (stream.Output.Reader.TryRead(out _))
        {
            throw new Exception("An invalid RPC envelope was sent to the peer.");
        }
    }

    private async Task CheckCancellationAndCloseAsync(CancellationToken stop)
    {
        var stream = new FrameStream();
        var link = CreateLink(stream);
        var requestId = services.Ids.GenId();
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
        await ThrowsAsync<IOException>(() => link.CallAsync<Empty>(new Empty(), services.Ids.GenId(), stop));
    }

    private async Task CheckTimeoutAsync(CancellationToken stop)
    {
        var stream = new FrameStream();
        var link = CreateLink(stream);
        var requestId = services.Ids.GenId();
        var pending = link.CallAsync<Empty>(new Empty(), requestId, stop, timeout: TimeSpan.FromMilliseconds(25));
        await stream.Output.Reader.ReadAsync(stop);
        await ThrowsAsync<TimeoutException>(() => pending);
        link.Receive(RpcFrames.Reply(new Empty(), requestId).Reply);

        var retry = link.CallAsync<Empty>(new Empty(), requestId, stop);
        await stream.Output.Reader.ReadAsync(stop);
        link.Receive(RpcFrames.Reply(new Empty(), requestId).Reply);
        await retry;
    }

    private async Task CheckBlockedWriteTimeoutAsync(CancellationToken stop)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new FrameStream { BeforeWrite = release.Task };
        var link = CreateLink(stream);
        var requestId = services.Ids.GenId();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var pending = link.CallAsync<Empty>(new Empty(), requestId, lifetime.Token, timeout: TimeSpan.FromMilliseconds(75));
        try
        {
            var guard = Task.Delay(TimeSpan.FromMilliseconds(500), stop);
            if (await Task.WhenAny(pending, guard) != pending)
            {
                throw new Exception("RPC timeout did not cover a blocked transport write.");
            }

            await ThrowsAsync<TimeoutException>(() => pending);
            if (stream.Output.Reader.TryPeek(out _))
            {
                throw new Exception("A timed-out RPC reached the blocked transport.");
            }
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await pending;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (TimeoutException)
            {
            }

            release.TrySetResult();
        }

        var retry = link.CallAsync<Empty>(new Empty(), requestId, stop);
        var request = await stream.Output.Reader.ReadAsync(stop);
        if (request.Request.RequestId != requestId)
        {
            throw new Exception("A blocked RPC timeout left a stale transport write.");
        }

        link.Receive(RpcFrames.Reply(new Empty(), requestId).Reply);
        await retry;
    }

    private PeerLink CreateLink(FrameStream stream) =>
        new(nameof(ProcessType.Login), ServerProcess.FormatName(nameof(ProcessType.Router), 1), services.Ids.GenId(), stream, services.Log);

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
