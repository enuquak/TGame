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
            case Frame.BodyOneofCase.Reply:
                link.Receive(frame.Reply);
                return;

            case Frame.BodyOneofCase.Request:
                if (!requests.TryWrite(frame))
                {
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "Peer request queue is full."));
                }

                return;

            default:
                throw new InvalidDataException("Invalid stream frame.");
        }
    }

    protected override Task ServeFrameAsync(Frame frame, string peer, string id, PeerLink link, CancellationToken stop) =>
        messages.DispatchAsync(frame.Request, peer, link, stop);
}
