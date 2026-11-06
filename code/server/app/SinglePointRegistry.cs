using System.Reflection;
using TGame.Server.Logic;

namespace TGame.PingPong;

internal sealed class SinglePointRegistry
{
    private readonly Dictionary<ProcessType, Type> bindings = [];

    public SinglePointRegistry(IEnumerable<Type> loadedTypes)
    {
        foreach (var type in loadedTypes)
        {
            var label = type.GetCustomAttribute<SinglePointForAttribute>(inherit: false);
            if (label == null)
            {
                if (!type.IsAbstract && type.IsSubclassOf(typeof(ServerSinglePoint)))
                    throw new InvalidOperationException($"Missing SinglePointFor on {type.FullName}.");
                continue;
            }
            if (!Enum.IsDefined(label.Role) || !type.IsSubclassOf(typeof(ServerSinglePoint)) ||
                type.IsAbstract || type.ContainsGenericParameters || type.GetConstructor(Type.EmptyTypes) == null)
                throw new InvalidOperationException($"Invalid SinglePoint class {type.FullName} for {label.Role}. Expected a concrete ServerSinglePoint with a public parameterless constructor.");
            if (!bindings.TryAdd(label.Role, type))
                throw new InvalidOperationException($"Duplicate SinglePoint for {label.Role}: {bindings[label.Role].FullName} and {type.FullName}.");
        }

        foreach (var role in Enum.GetValues<ProcessType>())
            if (!bindings.ContainsKey(role)) throw new InvalidOperationException($"Missing SinglePoint for {role}.");
    }

    public ServerSinglePoint Create(ProcessType role) =>
        (ServerSinglePoint)Activator.CreateInstance(bindings[role])!;
}
