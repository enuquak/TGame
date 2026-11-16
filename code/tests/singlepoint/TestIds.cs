using TGame.Server.Engine;

internal static class TestIds
{
    // Isolated in-memory tests do not publish IDs into a running server group.
    internal static readonly IIdGenerator Generator = new SnowflakeIdGenerator(0, TimeProvider.System);
}
