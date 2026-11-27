using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using TGame;
using TGame.Server;
using TGame.Server.Engine;

internal static class IocChecks
{
    internal static async Task RunAsync()
    {
        using var firstLogLifetime = new GameLog();
        using var secondLogLifetime = new GameLog();
        await using var first = CreateProvider(firstLogLifetime, 1);
        await using var second = CreateProvider(secondLogLifetime, 2);
        var firstLog = first.GetRequiredService<GameLog>();
        var secondLog = second.GetRequiredService<GameLog>();
        var firstConfiguration = first.GetRequiredService<ServerConfiguration>();
        Check(ReferenceEquals(firstConfiguration, first.GetRequiredService<ServerConfiguration>()),
            "YAML configuration is not a singleton in its container.");
        Check(!ReferenceEquals(firstConfiguration, second.GetRequiredService<ServerConfiguration>()),
            "Independent containers share their YAML configuration.");
        Check(ReferenceEquals(firstConfiguration.Group, first.GetRequiredService<ServerGroup>()),
            "The node startup group does not come from its shared configuration.");
        Check(ReferenceEquals(firstLog, firstLogLifetime), "The container replaced the startup logger.");
        Check(!ReferenceEquals(firstLog, secondLog), "Independent containers share a logger.");

        var firstIds = first.GetRequiredService<IdGenerator>();
        var secondIds = second.GetRequiredService<IdGenerator>();
        await firstIds.InitializeAsync();
        await secondIds.InitializeAsync();
        Check(ReferenceEquals(firstIds, first.GetRequiredService<IdGenerator>()), "ID service is not shared within its container.");
        Check(!ReferenceEquals(firstIds, secondIds), "Independent containers share an ID generator.");
        Check(firstIds.GenId() != secondIds.GenId(), "Different worker instances generated the same ID.");

        var firstZk = first.GetRequiredService<ZooKeeperProxy>();
        var secondZk = second.GetRequiredService<ZooKeeperProxy>();
        Check(ReferenceEquals(firstZk, first.GetRequiredService<ZooKeeperProxy>()), "ZK service is not shared within its container.");
        Check(!ReferenceEquals(firstZk, secondZk), "Independent containers share a ZK proxy.");
        var firstNode = first.GetRequiredService<ServerNode>();
        var secondNode = second.GetRequiredService<ServerNode>();
        Check(firstNode.Ready && secondNode.Ready, "Router resolved from the actual registrations is not ready.");
        Check(firstNode.Name != secondNode.Name && !ReferenceEquals(firstNode, secondNode), "Containers share process identity or node state.");
        Check(ReferenceEquals(firstNode, first.GetRequiredService<ServerNode>()), "Node is not shared within its container.");

        await first.DisposeAsync();
        Expect<ObjectDisposedException>(() => firstIds.GenId());
        await ExpectAsync<ObjectDisposedException>(() => firstZk.InitializeAsync());
        Check(IdText.IsValid(secondIds.GenId()), "Disposing one container invalidated another container's ID service.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await ExpectAsync<OperationCanceledException>(() => secondZk.InitializeAsync(cancelled.Token));
        await first.DisposeAsync();
        Check(IdText.IsValid(secondIds.GenId()), "Repeated disposal invalidated another container.");

        var originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            // 外部创建的启动日志必须活过容器，才能记录初始化或释放失败。
            firstLog.Error(ServerLogEvents.ProcessFailed, nameof(ProcessType.Router), detail: "容器退出后的启动错误", bothPrintConsole: true);
            using var failedLog = new GameLog();
            var registrations = ProcessRegistrations(failedLog);
            await using (var failed = registrations.BuildServiceProvider())
            {
                Expect<InvalidOperationException>(() => failed.GetRequiredService<ServerNode>());
            }

            failedLog.Error(ServerLogEvents.ProcessFailed, nameof(ProcessType.Router), detail: "身份未认领导致初始化失败", bothPrintConsole: true);
            var details = new HashSet<string?>();
            foreach (var row in error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(row);
                details.Add(document.RootElement.GetProperty(ServerLogFields.Detail).GetString());
            }

            Check(details.Contains("容器退出后的启动错误") && details.Contains("身份未认领导致初始化失败"),
                "Startup logging was lost after container failure/disposal.");
        }
        finally
        {
            Console.SetError(originalError);
        }

        Console.WriteLine("PASS actual IoC registrations, shared instances, independent containers, asynchronous disposal and startup error logging.");
    }

    private static ServiceProvider CreateProvider(GameLog log, int workerId)
    {
        var services = ProcessRegistrations(log);
        services.AddSingleton(new ServerProcess(ProcessType.Router, workerId));
        services.AddSingleton<IdGenerator>(_ => new IdGenerator(token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new SnowflakeIdGenerator(workerId, TimeProvider.System));
        }));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private static ServiceCollection ProcessRegistrations(GameLog log)
    {
        var services = new ServiceCollection();
        var configuration = TestNodes.LoadConfiguration();
        var options = TestServices.LocalZooKeeperOptions() with { Endpoint = "127.0.0.1:invalid-port", DiscoveryRoot = "/ioc-checks" };
        var idOptions = new IdGeneratorOptions { WorkerRootPath = "/ioc-workers" };
        ServerServices.AddProcessServices(services, configuration, log);
        Check(services.Count(service => service.ServiceType == typeof(ServerConfiguration)) == 1,
            "Process registrations contain multiple configuration services.");
        services.AddSingleton(options);
        services.AddSingleton(idOptions);
        return services;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
