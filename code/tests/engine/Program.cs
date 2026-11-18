using TGame.Server.Engine;

const int TestTimeoutSeconds = 5;
const int ExternalTimeoutSeconds = 60;
const int TestWorkerId = 0;

try
{
    if (args is ["--logging"])
    {
        LogLevelChecks.Run();
        return 0;
    }

    using var logLifetime = GameLog.Initialize();
    if (args is ["--configuration"])
    {
        ServerConfigurationChecks.Run();
        return 0;
    }

    if (args is ["--profile-storage", var profileIdConfig])
    {
        using var profileDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(ExternalTimeoutSeconds));
        await ProfileStorageChecks.RunAsync(profileIdConfig, profileDeadline.Token);
        return 0;
    }

    if (args is ["--mongo-storage"])
    {
        await MongoStorageChecks.RunAsync();
        return 0;
    }

    if (args is ["--id-zookeeper", var idConfig])
    {
        await using var localIdLifetime = await IdGenerator.InitializeAsync(
            _ => Task.FromResult(new SnowflakeIdGenerator(TestWorkerId, TimeProvider.System)));
        await IdGeneratorChecks.RunZooKeeperAsync(idConfig);
        return 0;
    }

    if (args is ["--registry"])
    {
        await ZooKeeperProxyChecks.RunAsync();
        return 0;
    }

    if (args is ["--zookeeper-session"])
    {
        await ZooKeeperProxyChecks.CheckSessionAsync();
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

    if (args is ["--connection-readiness"])
    {
        await StartupReadinessChecks.CheckWithoutDependenciesAsync(CancellationToken.None);
        return 0;
    }

    // 内存检查共用一个进程内生成器，不向真实服务器组发布 ID。
    await IdGeneratorLifecycleChecks.RunAsync();
    await using var idLifetime = await IdGenerator.InitializeAsync(
        _ => Task.FromResult(new SnowflakeIdGenerator(TestWorkerId, TimeProvider.System)));
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(
        args is ["--startup-publication"] ? ExternalTimeoutSeconds : TestTimeoutSeconds));
    var stop = deadline.Token;

    if (args is ["--rpc"])
    {
        RpcChecks.CheckTransportBoundary();
        await RpcChecks.RunAsync(stop);
        return 0;
    }

    if (args is ["--startup-publication"])
    {
        await StartupPublicationChecks.RunAsync(stop);
        return 0;
    }

    if (args is ["--discovery-links"])
    {
        await DiscoveryLinkChecks.RunAsync(stop);
        return 0;
    }

    if (args is ["--dynamic-topology"])
    {
        await DynamicTopologyChecks.RunAsync(stop);
        return 0;
    }

    ServerConfigurationChecks.Run();
    SettingsChecks.Run();
    GroupChecks.Run();
    await IdGeneratorChecks.RunAsync();
    RpcChecks.CheckTransportBoundary();
    await RpcChecks.RunAsync(stop);
    await StartupReadinessChecks.RunAsync(stop);
    await NodeCommunicationChecks.RunAsync(stop);
    Console.WriteLine("PASS engine process readiness, handshake and direct peer RPC.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
