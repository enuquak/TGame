using TGame.Management;
using TGame.Transport;

namespace TGame.Server;

internal sealed partial class PeerLink
{
    public async Task<GmPingReply> GmPingAsync(GmPingRequest request, CancellationToken stop)
    {
        var frame = await CallAsync(new Frame { GmPingRequest = request }, request.RequestId, request.EntityId, ServerLogEvents.GMPingTx, stop);
        if (frame.GmPingReply?.EntityId != request.EntityId)
        {
            throw new InvalidDataException("GM reply entity mismatch.");
        }

        return frame.GmPingReply;
    }

    public async Task ReplyAsync(GmPingReply reply, CancellationToken stop)
    {
        await WriteAsync(new Frame { GmPingReply = reply }, stop);
        TraceLog.Event(ServerLogEvents.GMPongTx, role, peer, reply.RequestId, reply.EntityId, id);
    }
}
