using TGame.Server.Engine;
using TGame.Management;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode
{
    internal Task<GmPingReply> GmPingAsync(GmPingRequest request, CancellationToken stop) =>
        ForwardGmPingAsync(ServerProcess.FormatName(nameof(ProcessType.Router), 1), request, stop);

    internal async Task<GmPingReply> RouteGmPingAsync(string from, GmPingRequest request, CancellationToken stop)
    {
        if (!Ready)
        {
            return GmUnavailable(request);
        }

        var expectedSender = Role == nameof(ProcessType.Router) ? nameof(ProcessType.GM) : nameof(ProcessType.Router);
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || !Guid.TryParseExact(request.EntityId, "N", out _) || PeerRole(from) != expectedSender)
        {
            throw new InvalidDataException("Invalid GM entity ping or ingress.");
        }

        if (localEntities.ContainsKey(request.EntityId))
        {
            return new GmPingReply { RequestId = request.RequestId, EntityId = request.EntityId, Responder = Role };
        }

        if (Role == nameof(ProcessType.Router) && owners.TryGetValue(request.EntityId, out var owner))
        {
            return await ForwardGmPingAsync(owner, request, stop);
        }

        return new GmPingReply { RequestId = request.RequestId, EntityId = request.EntityId, Status = GmPingReply.Types.Status.NotFound };
    }

    private async Task<GmPingReply> ForwardGmPingAsync(string peer, GmPingRequest request, CancellationToken stop)
    {
        if (!Ready || !links.TryGetValue(peer, out var link))
        {
            return GmUnavailable(request);
        }

        try
        {
            return await link.GmPingAsync(request, stop);
        }
        catch (Exception error) when (!stop.IsCancellationRequested && IsDeliveryFailure(error))
        {
            TraceLog.Event(ServerLogEvents.GMDeliveryFailed, Role, peer, request.RequestId, request.EntityId, detail: error.Message);
            return GmUnavailable(request);
        }
    }

    private static GmPingReply GmUnavailable(GmPingRequest request) => new()
    {
        RequestId = request.RequestId,
        EntityId = request.EntityId,
        Status = GmPingReply.Types.Status.Unavailable
    };
}
