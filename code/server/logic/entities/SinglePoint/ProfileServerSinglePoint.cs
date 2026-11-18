using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using MongoDB.Driver;
using TGame.Profile;
using TGame.Server.Engine;
using Result = TGame.Profile.ProfileReply.Types.Result;

namespace TGame.Server.Logic;

[SinglePointFor(ProcessType.Profile)]
internal sealed class ProfileServerSinglePoint : ServerSinglePoint, IDisposable
{
    private ProfileStore store = null!;

    protected override async Task InitializeRoleAsync(CancellationToken stop)
    {
        store = await ProfileStore.OpenAsync(stop);
        GameLog.Info(ServerLogEvents.InitStepCompleted, nameof(ProcessType.Profile), entity: EntityId,
            detail: $"{nameof(ProfileStore)} 初始化完成");
    }

    public override async Task<IMessage> HandleRequestAsync(EntityRpcContext context, Any payload, CancellationToken stop)
    {
        if (!payload.Is(ProfileRequest.Descriptor))
        {
            return await base.HandleRequestAsync(context, payload, stop);
        }

        var request = payload.Unpack<ProfileRequest>();
        if (request.OperationCase != ProfileRequest.OperationOneofCase.Fetch ||
            !IdText.IsValid(request.RequestId) ||
            !IdText.IsValid(request.Fetch))
        {
            return Failure(request, Result.Rejected);
        }

        GameLog.Debug(ProfileLogEvents.RequestRx, nameof(ProcessType.Profile), context.SourceProcess,
            request.RequestId, request.Fetch);
        try
        {
            var reply = await store.HandleAsync(request, stop);
            GameLog.Debug(ProfileLogEvents.ReplyTx, nameof(ProcessType.Profile), context.SourceProcess,
                reply.RequestId, reply.EntityId);
            return reply;
        }
        catch (Exception error) when (error is MongoException or TimeoutException)
        {
            GameLog.Error(ProfileLogEvents.DbError, nameof(ProcessType.Profile), id: request.RequestId,
                entity: ProfileStore.Subject(request), detail: error.GetType().Name);
            return Failure(request, Result.Unavailable);
        }
    }

    private static ProfileReply Failure(ProfileRequest request, Result result) => new()
    {
        RequestId = request.RequestId,
        EntityId = ProfileStore.Subject(request),
        Result = result
    };

    public void Dispose() => store?.Dispose();
}
