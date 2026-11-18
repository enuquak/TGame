using TGame.Server.Engine;

namespace TGame.Server;

internal static class ServerServices
{
    internal static void AddProcessServices(IServiceCollection services, ServerSettings settings,
        ServerConfiguration configuration, ZooKeeperOptions zooKeeperOptions, IdGeneratorOptions idOptions,
        GameLog log)
    {
        services.AddSingleton(settings);
        services.AddSingleton(configuration);
        services.AddSingleton(zooKeeperOptions);
        services.AddSingleton(idOptions);

        // 日志先于配置和容器创建，仍由 App 在所有资源退出后释放。
        services.AddSingleton(log);
        services.AddSingleton<ZooKeeperProxy>();
        services.AddSingleton<IdGenerator>();
        services.AddSingleton(provider => provider.GetRequiredService<ZooKeeperProxy>().Process);
        services.AddSingleton<ServerNode>();
    }
}
