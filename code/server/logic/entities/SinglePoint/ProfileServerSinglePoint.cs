using MongoDB.Driver;
using TGame.Profile;
using TGame.Server.Engine;

namespace TGame.Server.Logic;

[SinglePointFor(ProcessType.Profile)]
internal sealed class ProfileServerSinglePoint : ServerSinglePoint, IDisposable
{
    private ProfileStore store = null!;

    protected override async Task InitializeRoleAsync(CancellationToken stop) =>
        store = await ProfileStore.OpenAsync(stop);

    internal async Task<ProfileReply> HandleAsync(ProfileRequest request, CancellationToken stop)
    {
        try
        {
            return await store.HandleAsync(request, stop);
        }
        catch (Exception error) when (error is MongoException or TimeoutException)
        {
            GameLog.Error(ProfileLogEvents.DbError, nameof(ProcessType.Profile), id: request.RequestId,
                entity: ProfileStore.Subject(request), detail: error.GetType().Name);
            return ProfileMessages.Failure(request, ProfileReply.Types.Result.Unavailable);
        }
    }

    public void Dispose() => store?.Dispose();
}
