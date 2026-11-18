using Grpc.Core;
using TGame.Transport;

namespace TGame.Server.Engine;

public sealed class LinkService(ServerNode node) : ServerLink.ServerLinkBase
{
    public override async Task Connect(IAsyncStreamReader<Frame> input, IServerStreamWriter<Frame> output, ServerCallContext context)
    {
        var peer = context.RequestHeaders.GetValue(TransportProtocol.PeerHeader) ?? "";
        var link = context.RequestHeaders.GetValue(TransportProtocol.LinkHeader) ?? "";
        if (!IdText.IsValid(link) || !node.AcceptsIncoming(peer))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Unexpected link."));
        }

        if (!await ZooKeeperProxy.IsProcessClaimedAsync(peer, context.CancellationToken))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Peer identity is not claimed."));
        }

        if (!node.Ready)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "The receiving process is not ready."));
        }

        await node.RunLinkAsync(peer, link, input, output, context.CancellationToken, requireReady: true);
    }
}
