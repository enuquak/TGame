using TGame.Server.Engine;
using TGame.Profile;

namespace TGame.Server.Logic;

internal static partial class PeerLinkMessages
{
    public static async Task<ProfileReply> ProfileAsync(this PeerLink link, ProfileRequest request, CancellationToken stop)
    {
        var subject = ProfileStore.Subject(request);
        var reply = await link.CallAsync<ProfileReply>(request, request.RequestId, stop,
            () => TraceLog.Event(ServerLogEvents.ProfileRequestTx, link.Role, link.Peer, request.RequestId, subject, link.Id));
        TraceLog.Event(ServerLogEvents.ProfileReplyRx, link.Role, link.Peer, reply.RequestId, reply.EntityId, link.Id);

        if (reply.RequestId != request.RequestId || reply.EntityId != subject)
        {
            throw new InvalidDataException("Profile reply request or entity mismatch.");
        }

        return reply;
    }

    public static async Task ReplyAsync(this PeerLink link, ProfileReply reply, CancellationToken stop)
    {
        await link.ReplyAsync(reply, reply.RequestId, stop);
        TraceLog.Event(ServerLogEvents.ProfileReplyTx, link.Role, link.Peer, reply.RequestId, reply.EntityId, link.Id);
    }
}
