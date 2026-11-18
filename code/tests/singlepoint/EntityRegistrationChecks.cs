using Grpc.Core;
using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal static class EntityRegistrationChecks
{
    private enum InitializationOutcome
    {
        Success,
        RegistrationRejected,
        BusinessFailed
    }

    public static async Task RunAsync(CancellationToken stop)
    {
        await CheckRouterCreationAsync(disconnectBeforeCreation: false, stop);
        await CheckRouterCreationAsync(disconnectBeforeCreation: true, stop);
        await CheckRegistrationWaitAsync(stop);
        await CheckInitializationAsync(InitializationOutcome.Success, stop);
        await CheckInitializationAsync(InitializationOutcome.RegistrationRejected, stop);
        await CheckInitializationAsync(InitializationOutcome.BusinessFailed, stop);
        Console.WriteLine("PASS SinglePoint-owned registration: handshake isolation, acknowledgement, rejection, failed initialization and reconnect.");
    }

    private static async Task CheckRouterCreationAsync(bool disconnectBeforeCreation, CancellationToken stop)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Router] = 1;
        counts[ProcessType.Profile] = 1;
        using var configuration = TestNodes.InitializeConfiguration(new ServerGroup(counts));
        using var router = TestNodes.Create(nameof(ProcessType.Router));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var registrationLifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var profile = ServerProcess.FormatName(nameof(ProcessType.Profile), 1);
        var entityId = NewId();
        var stream = new FrameStream();
        stream.Input.Writer.TryWrite(TestNodes.Hello(profile));
        var running = router.RunLinkAsync(profile, NewId(), stream, stream, lifetime.Token);
        try
        {
            await stream.Output.Reader.ReadAsync(stop);
            await router.FirstLinks.WaitAsync(stop);
            var registration = TestNodes.RegisterAsync(stream, [entityId], registrationLifetime.Token);
            const int RegistrationObservationMilliseconds = 100;
            var observation = Task.Delay(RegistrationObservationMilliseconds, stop);
            Check(await Task.WhenAny(registration, observation) != registration,
                "Router acknowledged entity ownership before its SinglePoint was created.");
            Check(router.SinglePoint is null, "Entity registration created the Router SinglePoint before initialization.");

            if (disconnectBeforeCreation)
            {
                stream.Input.Writer.Complete();
                await running.WaitAsync(stop);
                registrationLifetime.Cancel();
                try
                {
                    await registration;
                    throw new Exception("Disconnected registration was acknowledged before Router creation.");
                }
                catch (OperationCanceledException) when (registrationLifetime.IsCancellationRequested)
                {
                }

                stream = new FrameStream();
                stream.Input.Writer.TryWrite(TestNodes.Hello(profile));
                running = router.RunLinkAsync(profile, NewId(), stream, stream, lifetime.Token);
                await stream.Output.Reader.ReadAsync(stop);
            }

            await router.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
            var point = (RouterServerSinglePoint)router.SinglePoint!;
            if (disconnectBeforeCreation)
            {
                Check(!point.IsRemoteEntityOwner(profile, entityId), "Disconnected registration left stale entity ownership.");
                registration = TestNodes.RegisterAsync(stream, [entityId], stop);
            }

            await registration.WaitAsync(stop);
            Check(point.IsRemoteEntityOwner(profile, entityId), "Router lost the registration that arrived before its SinglePoint.");
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

    private static async Task CheckRegistrationWaitAsync(CancellationToken stop)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Router] = 1;
        counts[ProcessType.Profile] = 1;
        using var configuration = TestNodes.InitializeConfiguration(new ServerGroup(counts));
        using var node = TestNodes.Create(nameof(ProcessType.Profile));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var router = ServerProcess.FormatName(nameof(ProcessType.Router), 1);
        var stream = new FrameStream { AcknowledgeRegistration = false };
        stream.Input.Writer.TryWrite(TestNodes.Hello(router));
        var running = node.RunLinkAsync(router, NewId(), stream, stream, lifetime.Token);
        try
        {
            await node.FirstLinks.WaitAsync(stop);
            var initialization = node.InitializeSinglePointAsync(new SinglePointRegistry([]), lifetime.Token);
            var registration = await stream.Output.Reader.ReadAsync(stop);
            const int TimeoutMarginMilliseconds = 100;
            await Task.Delay(PeerLink.RequestTimeout + TimeSpan.FromMilliseconds(TimeoutMarginMilliseconds), stop);
            Check(!initialization.IsCompleted, "Entity registration used the business RPC timeout while Router creation was pending.");

            stream.Input.Writer.TryWrite(RpcFrames.Reply(new RegisterEntitiesReply(), registration.Request.RequestId));
            await initialization.WaitAsync(stop);
            Check(node.Ready, "Delayed registration acknowledgement did not complete initialization.");
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

    private static async Task CheckInitializationAsync(InitializationOutcome outcome, CancellationToken stop)
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Router] = 1;
        counts[ProcessType.Profile] = 1;
        var group = new ServerGroup(counts);
        using var configuration = TestNodes.InitializeConfiguration(group);
        var profile = ServerProcess.FormatName(nameof(ProcessType.Profile), 1);
        var router = ServerProcess.FormatName(nameof(ProcessType.Router), 1);
        using var node = new ServerNode(profile);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var stream = new FrameStream { AcknowledgeRegistration = false };
        stream.Input.Writer.TryWrite(TestNodes.Hello(router));
        var running = node.RunLinkAsync(router, NewId(), stream, stream, lifetime.Token);
        try
        {
            await node.FirstLinks.WaitAsync(stop);
            Check(node.SinglePoint is null && !stream.Output.Reader.TryPeek(out _),
                "Engine handshake registered an entity before creating the SinglePoint.");

            var initialization = node.InitializeSinglePointAsync(new SinglePointRegistry([typeof(RegistrationWaitingSinglePoint)]), stop);
            var point = (RegistrationWaitingSinglePoint)node.SinglePoint!;
            await point.Started.Task.WaitAsync(stop);
            Check(IdText.IsValid(point.EntityId) && !point.LocalEntityIds.Any() && !stream.Output.Reader.TryPeek(out _),
                "Entities were published while business initialization was still pending.");

            if (outcome == InitializationOutcome.BusinessFailed)
            {
                point.Release.SetException(new InvalidDataException("Business initialization failed."));
                try
                {
                    await initialization.WaitAsync(stop);
                    throw new Exception("Business initialization failure was hidden.");
                }
                catch (InvalidDataException)
                {
                }

                Check(!point.Initialized && !node.Ready && !point.LocalEntityIds.Any() && !stream.Output.Reader.TryPeek(out _),
                    "Failed business initialization registered local or remote entities.");
                return;
            }

            point.Release.SetResult();
            var frame = await stream.Output.Reader.ReadAsync(stop);
            var request = frame.ReadRequest<RegisterEntitiesRequest>();
            Check(request.SinglePointId == point.EntityId && request.EntityIds.SequenceEqual([point.EntityId]),
                "SinglePoint registration advertised a different identity or entity list.");
            Check(!point.Initialized && !node.Ready && !initialization.IsCompleted,
                "SinglePoint initialization completed before registration was acknowledged.");

            if (outcome == InitializationOutcome.RegistrationRejected)
            {
                stream.Input.Writer.TryWrite(new Frame
                {
                    Reply = new RpcReply
                    {
                        RequestId = frame.Request.RequestId,
                        Error = new RpcError { StatusCode = (int)StatusCode.AlreadyExists, Detail = "Conflicting entity." }
                    }
                });
                try
                {
                    await running.WaitAsync(stop);
                    throw new Exception("Rejected entity registration kept its connection active.");
                }
                catch (RpcException error) when (error.StatusCode == StatusCode.AlreadyExists)
                {
                }

                Check(!point.Initialized && !node.Ready && !initialization.IsCompleted,
                    "Rejected registration completed initialization without a confirmed target.");
                stream = new FrameStream { AcknowledgeRegistration = false };
                stream.Input.Writer.TryWrite(TestNodes.Hello(router));
                running = node.RunLinkAsync(router, NewId(), stream, stream, lifetime.Token);
                frame = await stream.Output.Reader.ReadAsync(stop);
                Check(frame.ReadRequest<RegisterEntitiesRequest>().Equals(request), "Retry changed the pending SinglePoint identity.");
            }

            stream.Input.Writer.TryWrite(RpcFrames.Reply(new RegisterEntitiesReply(), frame.Request.RequestId));
            await initialization.WaitAsync(stop);
            Check(node.Ready, "Acknowledged SinglePoint did not become ready.");

            stream.Input.Writer.Complete();
            await running.WaitAsync(stop);
            Check(!node.Ready && node.FirstLinks.IsCompletedSuccessfully, "Disconnect did not revoke current readiness.");

            stream = new FrameStream { AcknowledgeRegistration = false };
            stream.Input.Writer.TryWrite(TestNodes.Hello(router));
            running = node.RunLinkAsync(router, NewId(), stream, stream, lifetime.Token);
            var retry = await stream.Output.Reader.ReadAsync(stop);
            Check(retry.ReadRequest<RegisterEntitiesRequest>().Equals(request) && !node.Ready,
                "Reconnect changed the identity or became ready before re-registration was acknowledged.");
            stream.Input.Writer.TryWrite(RpcFrames.Reply(new RegisterEntitiesReply(), retry.Request.RequestId));
            const int RegistrationPollMilliseconds = 10;
            while (!node.Ready)
            {
                await Task.Delay(RegistrationPollMilliseconds, stop);
            }
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

    private static string NewId() => IdGenerator.GenId();

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
}

[SinglePointFor(ProcessType.Profile)]
sealed class RegistrationWaitingSinglePoint : TestRequestSinglePoint
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task InitializeRoleAsync(CancellationToken stop)
    {
        Started.SetResult();
        return Release.Task.WaitAsync(stop);
    }
}
