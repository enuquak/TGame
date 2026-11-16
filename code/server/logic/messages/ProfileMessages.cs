using Grpc.Core;
using TGame.Profile;
using TGame.Server.Engine;
using Result = TGame.Profile.ProfileReply.Types.Result;

namespace TGame.Server.Logic;

internal static class ProfileMessages
{
    private const int MaxProfileEntityIdLength = 128;
    private const int MaxProfileTextLength = 80;

    public static void Register(RpcDispatcher messages, ServerNode node)
    {
        Func<EntityRpcContext, ProfileRequest, ProfileReply?>? validate = null;
        if (node.Role == nameof(ProcessType.Router))
        {
            validate = (context, request) => ValidateRoute(node, context, request);
        }

        messages.RegisterEntity<ProfileRequest, ProfileReply>(
            (context, request, stop) => HandleAsync(node, context, request, stop), validate);
    }

    public static async Task<ProfileReply> CallProfileAsync(this ServerNode node, ProfileRequest request, CancellationToken stop)
    {
        if (!node.Ready)
        {
            return Failure(request, Result.Unavailable);
        }

        var subject = ProfileStore.Subject(request);
        try
        {
            TraceLog.Event(ServerLogEvents.ProfileRequestTx, node.Role, id: request.RequestId, entity: subject);
            var reply = await node.CallSinglePointAsync<ProfileReply>(ProcessType.Profile, request, stop);
            TraceLog.Event(ServerLogEvents.ProfileReplyRx, node.Role, id: reply.RequestId, entity: reply.EntityId);
            if (reply.RequestId != request.RequestId || reply.EntityId != subject)
            {
                throw new InvalidDataException("Profile reply request or entity mismatch.");
            }

            return reply;
        }
        catch (Exception error) when (!stop.IsCancellationRequested &&
            error is IOException or RpcException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            TraceLog.Event(ServerLogEvents.ProfileDeliveryFailed, node.Role, id: request.RequestId, entity: subject, detail: error.GetType().Name);
            return Failure(request, Result.Unavailable);
        }
    }

    private static ProfileReply? ValidateRoute(ServerNode node, EntityRpcContext context, ProfileRequest request)
    {
        if (!node.Ready)
        {
            return Failure(request, Result.Unavailable);
        }

        bool canAccessProfiles = node.GetProcessType(context.SourceProcess) is ProcessType.Login or ProcessType.Base or ProcessType.Scene
            or ProcessType.DBMgr or ProcessType.Profile;
        if (!IsValid(request) ||
            !canAccessProfiles ||
            (request.OperationCase != ProfileRequest.OperationOneofCase.Fetch &&
                !node.IsEntityOwner(context.SourceProcess, ProfileStore.Subject(request))))
        {
            return Failure(request, Result.Rejected);
        }

        return null;
    }

    private static async Task<ProfileReply> HandleAsync(ServerNode node, EntityRpcContext context, ProfileRequest request, CancellationToken stop)
    {
        var subject = ProfileStore.Subject(request);
        TraceLog.Event(ServerLogEvents.ProfileRequestRx, node.Role, context.SourceProcess, request.RequestId, subject);
        if (!node.Ready)
        {
            return Failure(request, Result.Unavailable);
        }

        if (!IsValid(request) ||
            node.SinglePoint is not ProfileServerSinglePoint profile ||
            context.PeerProcess is null ||
            node.GetProcessType(context.PeerProcess) != ProcessType.Router)
        {
            return Failure(request, Result.Rejected);
        }

        var reply = await profile.HandleAsync(request, stop);
        TraceLog.Event(ServerLogEvents.ProfileReplyTx, node.Role, context.SourceProcess, reply.RequestId, reply.EntityId);
        return reply;
    }

    private static bool IsValid(ProfileRequest request)
    {
        var subject = ProfileStore.Subject(request);
        if (!IdText.IsValid(request.RequestId) ||
            subject.Length is 0 or > MaxProfileEntityIdLength ||
            !IdText.IsValid(subject))
        {
            return false;
        }

        if (request.Publish is not { } summary)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(summary.DisplayName) &&
            summary.DisplayName.Length <= MaxProfileTextLength &&
            summary.Title.Length <= MaxProfileTextLength;
    }

    internal static ProfileReply Failure(ProfileRequest request, Result result) => new()
    {
        RequestId = request.RequestId,
        EntityId = ProfileStore.Subject(request),
        Result = result
    };
}
