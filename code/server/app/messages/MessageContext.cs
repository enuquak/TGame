namespace TGame.Server;

internal sealed record MessageContext(ServerNode Node, string Peer, string LinkId, PeerLink Link);
