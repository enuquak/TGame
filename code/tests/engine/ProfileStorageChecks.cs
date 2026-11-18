using TGame.Profile;
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
        using var store = await ProfileStore.OpenAsync(stop);
        var subject = IdGenerator.GenId();
        try
        {
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Missing);
            await CheckAsync(new ProfileRequest
            {
                Publish = new EntitySummary { EntityId = subject, DisplayName = "storage check", Title = "initial" }
            }, Result.Stored);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "initial");
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "initial");
            await CheckAsync(new ProfileRequest
            {
                Publish = new EntitySummary { EntityId = subject, DisplayName = "storage check", Title = "updated" }
            }, Result.Stored);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Found, "updated");
            await CheckAsync(new ProfileRequest { Withdraw = subject }, Result.Removed);
            await CheckAsync(new ProfileRequest { Fetch = subject }, Result.Missing);
            Console.WriteLine("PASS independent Profile storage, MongoDB/Redis reads, cache invalidation and removal.");
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var removed = await store.HandleAsync(
                new ProfileRequest { RequestId = IdGenerator.GenId(), Withdraw = subject }, cleanup.Token);
            if (removed.Result != Result.Removed)
            {
                throw new Exception($"Could not clean up Profile test entity {subject}.");
            }
        }

        async Task CheckAsync(ProfileRequest request, Result expected, string? title = null)
        {
            request.RequestId = IdGenerator.GenId();
            var reply = await store.HandleAsync(request, stop);
            if (reply.RequestId != request.RequestId ||
                reply.EntityId != subject ||
                reply.Result != expected ||
                (title is not null && (reply.Summary?.Title != title || reply.Summary.DisplayName != "storage check")))
            {
                throw new Exception($"Unexpected Profile storage result: {reply}");
            }
        }
    }
}
