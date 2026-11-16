using TGame.Profile;
using TGame.Server.Engine;
using TGame.Transport;

namespace TGame.Server.Logic;

internal static partial class PeerLinkMessages
{
    public static async Task<ProfileReply> ProfileAsync(this PeerLink link, ProfileRequest request, CancellationToken stop)
    {
        var subject = ProfileStore.Subject(request);
        var frame = await link.CallAsync(new Frame { ProfileRequest = request }, request.RequestId, subject, ServerLogEvents.ProfileRequestTx, stop);
        if (frame.ProfileReply?.EntityId != subject)
        {
            throw new InvalidDataException("Profile reply entity mismatch.");
        }

        return frame.ProfileReply;
    }

    public static async Task ReplyAsync(this PeerLink link, ProfileReply reply, CancellationToken stop)
    {
        await link.WriteAsync(new Frame { ProfileReply = reply }, stop);
        TraceLog.Event(ServerLogEvents.ProfileReplyTx, link.Role, link.Peer, reply.RequestId, reply.EntityId, link.Id);
    }
}
