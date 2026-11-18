namespace TGame.Server.Engine;

public sealed class ServerConfiguration : IDisposable
{
    private static ServerConfiguration? instance;
    private readonly ServerGroup group;

    private ServerConfiguration(ServerGroup group)
    {
        this.group = group;
    }

    private static ServerGroup Group => Volatile.Read(ref instance)?.group
        ?? throw new InvalidOperationException("Initialize the server configuration before use.");

    public static IReadOnlyList<ServerProcess> Processes => Group.Processes;

    public static ServerProcess GetProcess(string name) => Group.GetProcess(name);

    public static ProcessType GetProcessType(string name) => GetProcess(name).Type;

    public static bool Contains(string name) => Group.Contains(name);

    public static ServerConfiguration Initialize(string path)
    {
        if (Volatile.Read(ref instance) is not null)
        {
            throw new InvalidOperationException("The server configuration is already initialized.");
        }

        return Initialize(ServerGroup.Load(path));
    }

    // 内存检查复用相同生命周期，配置数据仍由 ServerGroup 校验。
    internal static ServerConfiguration Initialize(ServerGroup group)
    {
        var initialized = new ServerConfiguration(group);
        if (Interlocked.CompareExchange(ref instance, initialized, null) is not null)
        {
            throw new InvalidOperationException("The server configuration is already initialized.");
        }

        return initialized;
    }

    public void Dispose()
    {
        Interlocked.CompareExchange(ref instance, null, this);
    }
}
