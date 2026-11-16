using System.Threading.Channels;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server;

internal sealed partial class ServerNode
{
    private static void ReceiveFrame(Frame frame, PeerLink link, ChannelWriter<Frame> requests)
    {
        switch (frame.BodyCase)
        {
            case Frame.BodyOneofCase.Reply:
                link.Receive(frame.Reply);
                break;

            case Frame.BodyOneofCase.Request:
                if (!requests.TryWrite(frame))
                {
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "Peer request queue is full."));
                }

                break;

            default:
                throw new InvalidDataException("Invalid stream frame.");
        }
    }
}
