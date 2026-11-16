using Grpc.Core;
using TGame.Transport;

namespace TGame.Server.Engine;

public sealed class LinkService(ServerNode node) : ServerLink.ServerLinkBase
{
    public override Task Connect(IAsyncStreamReader<Frame> input, IServerStreamWriter<Frame> output, ServerCallContext context)
    {
        var peer = context.RequestHeaders.GetValue(TransportProtocol.PeerHeader) ?? "";
        var link = context.RequestHeaders.GetValue(TransportProtocol.LinkHeader) ?? "";
        if (!IdText.IsValid(link) || !Topology.Dials.TryGetValue(peer, out var targets)
            || !targets.Contains(node.Role))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Unexpected link."));
        }

        var entities = context.RequestHeaders.GetValue(TransportProtocol.EntityHeader)?.Split(',') ?? [];
        return node.RunLinkAsync(peer, link, entities, input, output, context.CancellationToken);
    }
}
