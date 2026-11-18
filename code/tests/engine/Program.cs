using TGame.Server.Engine;

const int TestTimeoutSeconds = 5;
const int ExternalTimeoutSeconds = 60;

try
{
    if (args is ["--ioc"])
    {
        await IocChecks.RunAsync();
        return 0;
    }

    if (args is ["--logging"])
    {
        LogLevelChecks.Run();
        return 0;
    }

    await using var services = new TestServices();
    if (args is ["--configuration"])
    {
        ServerConfigurationChecks.Run(services);
        return 0;
    }

    if (args is ["--profile-storage", var profileIdConfig])
    {
        using var profileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(ExternalTimeoutSeconds));
        await ProfileStorageChecks.RunAsync(profileIdConfig, services.Log, profileDeadline.Token);
        return 0;
    }

    if (args is ["--mongo-storage"])
    {
        await MongoStorageChecks.RunAsync();
        return 0;
    }

    if (args is ["--id-zookeeper", var idConfig])
    {
        await services.Ids.InitializeAsync();
        await IdGeneratorChecks.RunZooKeeperAsync(idConfig, services);
        return 0;
    }

    if (args is ["--registry"])
    {
        await ZooKeeperProxyChecks.RunAsync(services.Log);
        return 0;
    }

    if (args is ["--zookeeper-session"])
    {
        await ZooKeeperProxyChecks.CheckSessionAsync(services.Log);
        await IdGeneratorLifecycleChecks.RunAsync();
        return 0;
    }

    if (args is not [] and not ["--engine"] and not ["--node"] and
        not ["--connection-readiness"] and not ["--dynamic-topology"] and
        not ["--discovery-links"] and not ["--startup-publication"] and not ["--rpc"])
    {
        Console.Error.WriteLine("Unknown test mode.");
        return 1;
    }

    // 内存检查共用一个进程内生成器，不向真实服务器组发布 ID。
    await services.Ids.InitializeAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(
        args is ["--startup-publication"] ? ExternalTimeoutSeconds : TestTimeoutSeconds));
    var stop = deadline.Token;

    if (args is ["--connection-readiness"])
    {
        await new StartupReadinessChecks(services).RunAsync(stop);
        return 0;
    }

    if (args is ["--rpc"])
    {
        new RpcChecks(services).CheckTransportBoundary();
        await new RpcChecks(services).RunAsync(stop);
        return 0;
    }

    if (args is ["--startup-publication"])
    {
        await new StartupPublicationChecks(services).RunAsync(stop);
        return 0;
    }

    if (args is ["--discovery-links"])
    {
        await new DiscoveryLinkChecks(services).RunAsync(stop);
        return 0;
    }

    if (args is ["--dynamic-topology"])
    {
        await new DynamicTopologyChecks(services).RunAsync(stop);
        return 0;
    }

    ServerConfigurationChecks.Run(services);
    SettingsChecks.Run();
    GroupChecks.Run();
    await IdGeneratorLifecycleChecks.RunAsync();
    await IdGeneratorChecks.RunAsync();
    new RpcChecks(services).CheckTransportBoundary();
    await new RpcChecks(services).RunAsync(stop);
    await new StartupReadinessChecks(services).RunAsync(stop);
    await new NodeCommunicationChecks(services).RunAsync(stop);
    Console.WriteLine("PASS engine process readiness, handshake and direct peer RPC.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
