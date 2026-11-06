using MongoDB.Driver;
using TGame.Profile;
using Result = TGame.Profile.ProfileReply.Types.Result;

namespace TGame.PingPong;

internal sealed partial class ProbeNode
{
    internal Task<ProfileReply> CallProfileAsync(ProfileRequest request, CancellationToken stop) =>
        links["Router"].ProfileAsync(request, stop);

    private async Task<ProfileReply> RouteProfileAsync(string from, ProfileRequest request, CancellationToken stop)
    {
        string subject = ProfileStore.Subject(request);
        ProfileReply Failure(Result result) => new() { RequestId = request.RequestId, EntityId = subject, Result = result };
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || subject.Length is 0 or > 128 ||
            !(Guid.TryParseExact(subject, "N", out _) || Topology.Entities.ContainsValue(subject)) ||
            (request.Publish is { } summary && (string.IsNullOrWhiteSpace(summary.DisplayName) ||
                summary.DisplayName.Length > 80 || summary.Title.Length > 80)))
            return Failure(Result.Rejected);

        if (role == "Router")
        {
            if (!Topology.Entities.ContainsKey(from) ||
                (request.OperationCase != ProfileRequest.OperationOneofCase.Fetch && owners.GetValueOrDefault(subject) != from))
                return Failure(Result.Rejected);
            if (!owners.TryGetValue(Topology.Entities["Profile"], out var owner) || !links.TryGetValue(owner, out var destination))
                return Failure(Result.Unavailable);
            return await destination.ProfileAsync(request, stop);
        }
        if (profileStore == null || from != "Router") return Failure(Result.Rejected);
        try { return await profileStore.HandleAsync(request, stop); }
        catch (Exception error) when (error is MongoException or TimeoutException)
        {
            TraceLog.Event("PROFILE_DB_ERROR", role, id: request.RequestId, entity: subject, detail: error.GetType().Name);
            return Failure(Result.Unavailable);
        }
    }

    // The loopback smoke driver can only change this run's Base SinglePoint summary.
    // Other Game entities use the same typed ProfileRequest over their Router stream.
    private Task<ProfileReply> RunProfileProbeAsync(Ping ping, CancellationToken stop)
    {
        var request = new ProfileRequest { RequestId = ping.RequestId };
        bool ownProbe = role == "Base" && ping.EntityId == singlePoint.State.EntityId && ping.ProfileSubject == ping.EntityId;
        switch (ping.ProfileProbe)
        {
            case Ping.Types.ProfileProbe.Publish when ownProbe:
                request.Publish = new EntitySummary { EntityId = ping.EntityId, DisplayName = "TGame probe", Title = "Profile smoke" };
                break;
            case Ping.Types.ProfileProbe.Withdraw when ownProbe:
                request.Withdraw = ping.EntityId; break;
            case Ping.Types.ProfileProbe.Fetch when role is "Login" or "Scene" && Guid.TryParseExact(ping.ProfileSubject, "N", out _):
                request.Fetch = ping.ProfileSubject; break;
            default: throw new InvalidDataException("Invalid Profile probe.");
        }
        return CallProfileAsync(request, stop);
    }
}
