namespace TGame.Server.Engine;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SinglePointForAttribute(ProcessType role) : Attribute
{
    public ProcessType Role { get; } = role;
}
