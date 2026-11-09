using TGame.Diagnostics;

namespace TGame.Server;

internal sealed partial class ServerNode
{
    // Only the GM HTTP handler constructs this command; it accepts no storage or profile operations.
    internal Task<Pong> GmPingAsync(Ping ping, CancellationToken stop) => ForwardPingAsync(nameof(ProcessType.Router), ping, stop);

    private async Task<Pong> RouteGmPingAsync(string from, Ping ping, CancellationToken stop)
    {
        var expectedSender = role == nameof(ProcessType.Router) ? nameof(ProcessType.GM) : nameof(ProcessType.Router);
        if (!Guid.TryParseExact(ping.RequestId, "N", out _) || !Guid.TryParseExact(ping.EntityId, "N", out _) ||
            ping.Storage || ping.ProfileProbe != Ping.Types.ProfileProbe.None ||
            from != expectedSender)
        {
            throw new InvalidDataException("Invalid GM ping or ingress.");
        }

        if (localEntities.ContainsKey(ping.EntityId))
        {
            return new Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Responder = role };
        }

        if (role == nameof(ProcessType.Router) && owners.TryGetValue(ping.EntityId, out var owner))
        {
            return await ForwardPingAsync(owner, ping, stop);
        }

        return new Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Status = Pong.Types.Status.NotFound };
    }
}
