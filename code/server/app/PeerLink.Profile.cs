using TGame.Profile;

namespace TGame.Server;

internal sealed partial class PeerLink
{
    public async Task<ProfileReply> ProfileAsync(ProfileRequest request, CancellationToken stop)
    {
        var subject = ProfileStore.Subject(request);
        var reply = await CallAsync<ProfileReply>(request, request.RequestId, stop,
            () => TraceLog.Event(ServerLogEvents.ProfileRequestTx, role, peer, request.RequestId, subject, id));
        TraceLog.Event(ServerLogEvents.ProfileReplyRx, role, peer, reply.RequestId, reply.EntityId, id);

        if (reply.RequestId != request.RequestId || reply.EntityId != subject)
        {
            throw new InvalidDataException("Profile reply request or entity mismatch.");
        }

        return reply;
    }

    public async Task ReplyAsync(ProfileReply reply, CancellationToken stop)
    {
        await ReplyAsync(reply, reply.RequestId, stop);
        TraceLog.Event(ServerLogEvents.ProfileReplyTx, role, peer, reply.RequestId, reply.EntityId, id);
    }
}
