namespace TGame.Server.Engine;

public sealed class SinglePointState(IIdGenerator idGenerator)
{
    public string EntityId { get; } = idGenerator.GenId();
}
