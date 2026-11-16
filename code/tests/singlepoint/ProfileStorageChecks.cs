using System.Globalization;
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
        var options = IdGeneratorOptions.Load(idConfig, ServerSettings.Current.Value.ZooKeeper);
        await using var ids = await SnowflakeIdGenerator.CreateAsync(options, stop);
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
        counts[ProcessType.Router] = 1;
        counts[ProcessType.Login] = 1;
        counts[ProcessType.Profile] = 1;
        var group = new ServerGroup(counts);
        var messages = new RpcDispatcher();
        using var node = new ServerNode(ServerProcess.FormatName(nameof(ProcessType.Profile), 1), group, messages, ids);
        ServerMessages.Register(messages, node);
        var stream = new FrameStream();
        var router = ServerProcess.FormatName(nameof(ProcessType.Router), 1);
        var source = ServerProcess.FormatName(nameof(ProcessType.Login), 1);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        stream.Input.Writer.TryWrite(TestNodes.Hello(router));
        var running = node.RunLinkAsync(router, NewId(), [], stream, stream, lifetime.Token);
        string subject = NewId();
        ProfileServerSinglePoint? point = null;
        try
        {
            await node.FirstLinks.WaitAsync(stop);
            await node.InitializeSinglePointAsync(new SinglePointRegistry(typeof(ServerMessages).Assembly.GetTypes()), stop);
            point = (ProfileServerSinglePoint)node.SinglePoint!;

            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Missing);
            await CheckAsync(new ProfileRequest
            {
                Publish = new EntitySummary { EntityId = subject, DisplayName = "1117 review", Title = "initial" }
            }, Result.Stored);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "initial");
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "initial");
            await CheckAsync(new ProfileRequest
            {
                Publish = new EntitySummary { EntityId = subject, DisplayName = "1117 review", Title = "updated" }
            }, Result.Stored);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "updated");

            var direct = await node.CallEntityAsync<ProfileReply>(node.State.EntityId,
                new ProfileRequest { RequestId = NewId(), Withdraw = subject }, stop);
            if (direct.Result != Result.Rejected)
            {
                throw new Exception("A local Profile call bypassed Router validation.");
            }

            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "updated");
            await CheckAsync(new ProfileRequest { Withdraw = subject }, Result.Removed);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Missing);
            Console.WriteLine("PASS real Profile RPC handler, MongoDB/Redis publish, cache read, update, withdraw and Router-only reception.");
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

            if (point is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var removed = await point.HandleAsync(new ProfileRequest { RequestId = NewId(), Withdraw = subject }, cleanup.Token);
                if (removed.Result != Result.Removed)
                {
                    throw new Exception($"Could not clean up Profile test entity {subject}.");
                }
            }
        }

        string NewId() => ids.GenId().ToString(CultureInfo.InvariantCulture);

        async Task CheckAsync(ProfileRequest request, Result expected, string? title = null)
        {
            request.RequestId = NewId();
            var frame = ProfileRpcChecks.Request(source, request);
            frame.Request.EntityId = node.State.EntityId;
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
