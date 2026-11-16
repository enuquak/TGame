namespace TGame.Server.Engine;

public sealed record EntityRpcContext(string RequestId, string SourceProcess, string EntityId, string? PeerProcess = null);
