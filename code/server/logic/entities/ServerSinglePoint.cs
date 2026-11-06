using TGame.Server.Model;

namespace TGame.Server.Logic;

internal enum ProcessType { Gate, Login, Base, Scene, Router, DBMgr, Profile, GM }

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
internal sealed class SinglePointForAttribute(ProcessType role) : Attribute
{
    public ProcessType Role { get; } = role;
}

internal abstract class ServerSinglePoint
{
    public SinglePointState State { get; } = new();

    public virtual void Initialize(Action<string> registerEntity, IReadOnlyList<string> peers, Action<string> connect)
    {
        registerEntity(State.EntityId);
        foreach (var peer in peers) connect(peer);
    }
}
