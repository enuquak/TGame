using System.Threading.Channels;
using Grpc.Core;
using TGame.Server.Engine;
using TGame.Transport;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode
{
    protected override void ReceiveFrame(Frame frame, PeerLink link, ChannelWriter<Frame> requests)
    {
        switch (frame.BodyCase)
        {
            case Frame.BodyOneofCase.Pong:
                link.Receive(frame, frame.Pong.RequestId, frame.Pong.EntityId, ServerLogEvents.PongRx);
                break;

            case Frame.BodyOneofCase.ProfileReply:
                link.Receive(frame, frame.ProfileReply.RequestId, frame.ProfileReply.EntityId, ServerLogEvents.ProfileReplyRx);
                break;

            case Frame.BodyOneofCase.Ping or Frame.BodyOneofCase.ProfileRequest:
                if (!requests.TryWrite(frame))
                {
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "Peer request queue is full."));
                }

                break;

            default:
                throw new InvalidDataException("Invalid stream frame.");
        }
    }

    protected override async Task ServeFrameAsync(Frame frame, string peer, string id, PeerLink link, CancellationToken stop)
    {
        if (frame.ProfileRequest is { } request)
        {
            TraceLog.Event(ServerLogEvents.ProfileRequestRx, Role, peer, request.RequestId, ProfileStore.Subject(request), id);
            await link.ReplyAsync(await RouteProfileAsync(peer, request, stop), stop);
            return;
        }

        var ping = frame.Ping;
        TraceLog.Event(ServerLogEvents.PingRx, Role, peer, ping.RequestId, ping.EntityId, id);

        try
        {
            await link.ReplyAsync(await RouteAsync(peer, ping, stop), stop);
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            TraceLog.Event(ServerLogEvents.PingFailed, Role, peer, ping.RequestId, ping.EntityId, id, error.Message);
            throw;
        }
    }
}
