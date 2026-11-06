namespace TGame.Server.Model;

internal sealed class SinglePointState
{
    public string EntityId { get; } = Guid.NewGuid().ToString("N");
}
