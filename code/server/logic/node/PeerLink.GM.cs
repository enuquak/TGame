using TGame.Management;
using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal static partial class PeerLinkMessages
{
    public static async Task<GmPingReply> GmPingAsync(this PeerLink link, GmPingRequest request, CancellationToken stop)
    {
        var reply = await link.CallAsync<GmPingReply>(request, request.RequestId, stop,
            () => TraceLog.Event(ServerLogEvents.GMPingTx, link.Role, link.Peer, request.RequestId, request.EntityId, link.Id));
        TraceLog.Event(ServerLogEvents.GMPongRx, link.Role, link.Peer, reply.RequestId, reply.EntityId, link.Id);

        if (reply.RequestId != request.RequestId || reply.EntityId != request.EntityId)
        {
            throw new InvalidDataException("GM reply request or entity mismatch.");
        }

        return reply;
    }

    public static async Task ReplyAsync(this PeerLink link, GmPingReply reply, CancellationToken stop)
    {
        await link.ReplyAsync(reply, reply.RequestId, stop);
        TraceLog.Event(ServerLogEvents.GMPongTx, link.Role, link.Peer, reply.RequestId, reply.EntityId, link.Id);
    }
}
