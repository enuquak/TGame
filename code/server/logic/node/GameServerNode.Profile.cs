using MongoDB.Driver;
using TGame.Profile;
using TGame.Server.Engine;
using Result = TGame.Profile.ProfileReply.Types.Result;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode
{
    private const int MaxProfileEntityIdLength = 128;
    private const int MaxProfileTextLength = 80;

    internal Task<ProfileReply> CallProfileAsync(ProfileRequest request, CancellationToken stop) =>
        ForwardProfileAsync(nameof(ProcessType.Router), request, stop);

    private async Task<ProfileReply> ForwardProfileAsync(string peer, ProfileRequest request, CancellationToken stop)
    {
        if (!Ready || !links.TryGetValue(peer, out var link))
        {
            return new ProfileReply { RequestId = request.RequestId, EntityId = ProfileStore.Subject(request), Result = Result.Unavailable };
        }

        try
        {
            return await link.ProfileAsync(request, stop);
        }
        catch (Exception error) when (!stop.IsCancellationRequested && IsDeliveryFailure(error))
        {
            TraceLog.Event(ServerLogEvents.ProfileDeliveryFailed, Role, peer, request.RequestId, ProfileStore.Subject(request), detail: error.GetType().Name);
        }

        return new ProfileReply { RequestId = request.RequestId, EntityId = ProfileStore.Subject(request), Result = Result.Unavailable };
    }

    internal async Task<ProfileReply> RouteProfileAsync(string from, ProfileRequest request, CancellationToken stop)
    {
        string subject = ProfileStore.Subject(request);
        ProfileReply Failure(Result result) => new()
        {
            RequestId = request.RequestId,
            EntityId = subject,
            Result = result
        };

        if (!Ready)
        {
            return Failure(Result.Unavailable);
        }

        if (!IdText.IsValid(request.RequestId) ||
            subject.Length is 0 or > MaxProfileEntityIdLength ||
            !IdText.IsValid(subject) ||
            (request.Publish is { } summary && (string.IsNullOrWhiteSpace(summary.DisplayName) ||
                summary.DisplayName.Length > MaxProfileTextLength ||
                summary.Title.Length > MaxProfileTextLength)))
        {
            return Failure(Result.Rejected);
        }

        if (Role == nameof(ProcessType.Router))
        {
            bool canAccessProfiles = from is nameof(ProcessType.Login) or nameof(ProcessType.Base) or nameof(ProcessType.Scene)
                or nameof(ProcessType.DBMgr) or nameof(ProcessType.Profile);
            if (!canAccessProfiles ||
                (request.OperationCase != ProfileRequest.OperationOneofCase.Fetch && owners.GetValueOrDefault(subject) != from))
            {
                return Failure(Result.Rejected);
            }

            return await ForwardProfileAsync(nameof(ProcessType.Profile), request, stop);
        }

        if (SinglePoint is not ProfileServerSinglePoint profile || from != nameof(ProcessType.Router))
        {
            return Failure(Result.Rejected);
        }

        try
        {
            return await profile.Store.HandleAsync(request, stop);
        }
        catch (Exception error) when (error is MongoException or TimeoutException)
        {
            TraceLog.Event(ServerLogEvents.ProfileDbError, Role, id: request.RequestId, entity: subject, detail: error.GetType().Name);
            return Failure(Result.Unavailable);
        }
    }
}
