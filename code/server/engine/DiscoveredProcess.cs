namespace TGame.Server.Engine;

public sealed record DiscoveredProcess(ServerProcess Process, string Address, long Generation);
