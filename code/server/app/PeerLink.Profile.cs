using TGame.Profile;
using TGame.Transport;

namespace TGame.Server;

internal sealed partial class PeerLink
{
    public async Task<ProfileReply> ProfileAsync(ProfileRequest request, CancellationToken stop)
    {
        var subject = ProfileStore.Subject(request);
        var frame = await CallAsync(new Frame { ProfileRequest = request }, request.RequestId, subject, ServerLogEvents.ProfileRequestTx, stop);
        if (frame.ProfileReply?.EntityId != subject)
        {
            throw new InvalidDataException("Profile reply entity mismatch.");
        }

        return frame.ProfileReply;
    }

    public async Task ReplyAsync(ProfileReply reply, CancellationToken stop)
    {
        await WriteAsync(new Frame { ProfileReply = reply }, stop);
        TraceLog.Event(ServerLogEvents.ProfileReplyTx, role, peer, reply.RequestId, reply.EntityId, id);
    }
}
