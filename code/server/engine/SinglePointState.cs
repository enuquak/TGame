namespace TGame.Server.Engine;

public sealed class SinglePointState
{
    public string EntityId { get; } = Guid.NewGuid().ToString("N");
}
