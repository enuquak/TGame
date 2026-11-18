using TGame;
using TGame.Profile;
using TGame.Server;
using TGame.Server.Engine;
using TGame.Server.Logic;
using Result = TGame.Profile.ProfileReply.Types.Result;

internal static class ProfileStorageChecks
{
    public static async Task RunAsync(string idConfig, CancellationToken stop)
    {
        var options = IdGeneratorOptions.Load(idConfig);
        await using var zooKeeperLifetime = await ZooKeeperProxy.InitializeAsync(ZooKeeperProxyChecks.LoadOptions(), stop);
        await using var idLifetime = await IdGenerator.InitializeAsync(options, stop);
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Router] = 1;
        counts[ProcessType.Login] = 1;
        counts[ProcessType.Profile] = 1;
        var group = new ServerGroup(counts);
        using var configuration = ServerConfiguration.Initialize(group);
        using var node = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Profile), 1));
        var stream = new FrameStream();
        var router = ServerProcess.FormatName(nameof(ProcessType.Router), 1);
        var source = ServerProcess.FormatName(nameof(ProcessType.Login), 1);
        using var fixtures = await ProfileStore.OpenAsync(stop);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        stream.Input.Writer.TryWrite(TestNodes.Hello(router));
        var running = node.RunLinkAsync(router, NewId(), stream, stream, lifetime.Token);
        string subject = NewId();
        try
        {
            await node.FirstLinks.WaitAsync(stop);
            await node.InitializeSinglePointAsync(new SinglePointRegistry(typeof(ProfileServerSinglePoint).Assembly.GetTypes()), stop);

            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Missing);
            await SeedAsync(new ProfileRequest
            {
                Publish = new EntitySummary { EntityId = subject, DisplayName = "1117 review", Title = "initial" }
            }, Result.Stored);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "initial");
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "initial");
            await SeedAsync(new ProfileRequest
            {
                Publish = new EntitySummary { EntityId = subject, DisplayName = "1117 review", Title = "updated" }
            }, Result.Stored);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "updated");

            var direct = await node.CallEntityAsync<ProfileReply>(node.SinglePoint!.EntityId,
                new ProfileRequest { RequestId = NewId(), Withdraw = subject }, stop);
            if (direct.Result != Result.Rejected)
            {
                throw new Exception("A local Profile call accepted a write.");
            }

            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "updated");
            await CheckAsync(new ProfileRequest { Withdraw = subject }, Result.Rejected);
            await CheckAsync(new ProfileRequest
            {
                Publish = new EntitySummary { EntityId = subject, DisplayName = "forbidden", Title = "forbidden" }
            }, Result.Rejected);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "updated");
            await SeedAsync(new ProfileRequest { Withdraw = subject }, Result.Removed);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Missing);
            Console.WriteLine("PASS read-only Profile RPC, MongoDB/Redis query and cache read, fixture update and rejected writes.");
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

            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var removed = await fixtures.HandleAsync(new ProfileRequest { RequestId = NewId(), Withdraw = subject }, cleanup.Token);
            if (removed.Result != Result.Removed)
            {
                throw new Exception($"Could not clean up Profile test entity {subject}.");
            }
        }

        string NewId() => IdGenerator.GenId();

        async Task SeedAsync(ProfileRequest request, Result expected)
        {
            request.RequestId = NewId();
            var reply = await fixtures.HandleAsync(request, stop);
            if (reply.Result != expected)
            {
                throw new Exception($"Unexpected Profile fixture result: {reply}");
            }
        }

        async Task CheckAsync(ProfileRequest request, Result expected, string? title = null)
        {
            request.RequestId = NewId();
            var frame = ProfileRpcChecks.Request(source, request);
            frame.Request.EntityId = node.SinglePoint!.EntityId;
            stream.Input.Writer.TryWrite(frame);
            var response = await stream.Output.Reader.ReadAsync(stop);
            var reply = response.ReadReply<ProfileReply>();
            if (response.Reply.RequestId != frame.Request.RequestId ||
                reply.RequestId != request.RequestId ||
                reply.EntityId != subject ||
                reply.Result != expected ||
                (title is not null && (reply.Summary?.Title != title || reply.Summary.DisplayName != "1117 review")))
            {
                throw new Exception($"Unexpected Profile result: {reply}");
            }
        }
    }
}
