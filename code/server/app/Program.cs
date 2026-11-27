using System.Net;
using System.Diagnostics;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using TGame;
using TGame.Server;
using TGame.Server.Engine;

const int ProcessArgumentCount = 1;
const int RegistrationTimeoutSeconds = 40;
const int ShutdownTimeoutSeconds = 3;
const int StopPollIntervalMilliseconds = 100;

if (args.Length != ProcessArgumentCount)
{
    throw new ArgumentException("Usage: TGame.Server <process-type>");
}

Serilog.Debugging.SelfLog.Enable(TextWriter.Synchronized(Console.Error));

using var log = CreateLog();
var processType = ServerProcess.ParseType(args[0]);

try
{
    await RunServerAsync(processType, log);
}
catch (Exception error)
{
    log.Error(ServerLogEvents.ProcessFailed, processType.ToString(), detail: error.ToString(), bothPrintConsole: true);
    Environment.ExitCode = 1;
}
finally
{
    Serilog.Debugging.SelfLog.Disable();
}

async Task RunServerAsync(ProcessType processType, GameLog log)
{
    var role = processType.ToString();
    var configuration = ServerConfiguration.LoadSelected();
    log.SetMinimumLevel(configuration.LoggingMinimumLevel);
    var runDirectory = Environment.GetEnvironmentVariable(EngineEnvironment.RunDirectory);
    if (string.IsNullOrWhiteSpace(runDirectory))
    {
        runDirectory = configuration.RunDirectory;
    }

    var runDir = Path.GetFullPath(runDirectory, Directory.GetCurrentDirectory());
    if (!Directory.Exists(runDir))
    {
        throw new DirectoryNotFoundException($"Runtime directory does not exist. Prepare it with the server scripts: {runDir}");
    }

    log.Info(ServerLogEvents.InitStepCompleted, role, detail: "配置加载完成");

    await using var app = CreateApplication(configuration, log);
    log.Info(ServerLogEvents.InitStepCompleted, role, detail: "app 服务注册及构建完成");
    var zooKeeper = app.Services.GetRequiredService<ZooKeeperProxy>();
    var process = await InitializeZooKeeperAsync(zooKeeper, processType);

    var identityKey = WriteProcessIdentity(runDir, process);
    log.OpenServer(process);
    log.Info(ServerLogEvents.InitStepCompleted, role, detail: $"ZK 实例身份 {process.Name} 已占用，类型连接配置已加载");

    var ids = app.Services.GetRequiredService<IdGenerator>();
    await ids.InitializeAsync();
    log.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: $"{nameof(IdGenerator)} 单例初始化完成");

    var node = app.Services.GetRequiredService<ServerNode>();
    log.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: $"{nameof(ServerNode)} 节点创建完成");
    await InitializeApplicationAsync(app, process, log);

    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    var stop = lifetime.Token;
    var workers = new List<Task>();
    try
    {
        workers.Add(SuperviseAsync(WatchStopAsync(runDir, process.Name, identityKey, lifetime), lifetime));
        log.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "停止信号监控已启动");

        workers.Add(SuperviseAsync(WatchZooKeeperSessionAsync(zooKeeper, stop), lifetime));
        log.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "ZooKeeper 会话监控已启动");

        workers.Add(SuperviseAsync(node.MaintainConnectionsAsync(stop), lifetime));
        log.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "动态发现与连接维护已启动");

        await node.FirstLinks.WaitAsync(stop);
        log.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "引擎层初始化完成");

        using (var publicationTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            publicationTimeout.CancelAfter(TimeSpan.FromSeconds(RegistrationTimeoutSeconds));
            await zooKeeper.RegisterProcessAsync(process, app.Urls.Single(), publicationTimeout.Token);
        }

        log.Info(ServerLogEvents.ProcessReady, process.Role, detail: "进程初始化完成");
        await Task.WhenAll(workers);
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
    }
    finally
    {
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            await app.StopAsync(TimeSpan.FromSeconds(ShutdownTimeoutSeconds));
        }
    }
}

static GameLog CreateLog()
{
    var directory = Environment.GetEnvironmentVariable(EngineEnvironment.RunDirectory);
    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
    {
        directory = Environment.GetEnvironmentVariable(EngineEnvironment.LogRoot) ?? Path.Combine("logs", "server");
    }

    using var process = Process.GetCurrentProcess();
    return new GameLog(ServerRunFiles.StartupLogDirectory(directory, process.Id, process.StartTime.ToUniversalTime().Ticks));
}

static string WriteProcessIdentity(string runDir, ServerProcess process)
{
    using var currentProcess = Process.GetCurrentProcess();
    var identityKey = ServerRunFiles.ProcessKey(Environment.ProcessId, currentProcess.StartTime.ToUniversalTime().Ticks);
    var identityPath = Path.Combine(runDir, ServerRunFiles.IdentityPrefix + identityKey);
    File.WriteAllText(identityPath + ".tmp", process.Name);
    File.Move(identityPath + ".tmp", identityPath, true);
    return identityKey;
}

static async Task<ServerProcess> InitializeZooKeeperAsync(ZooKeeperProxy zooKeeper, ProcessType processType)
{
    await zooKeeper.InitializeAsync();
    using var claimTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(RegistrationTimeoutSeconds));
    return await zooKeeper.ClaimProcessAsync(processType, claimTimeout.Token);
}

static WebApplication CreateApplication(ServerConfiguration configuration, GameLog log)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddGrpc();
    ServerServices.AddProcessServices(builder.Services, configuration, log);
    return builder.Build();
}

static async Task InitializeApplicationAsync(WebApplication app, ServerProcess process, GameLog log)
{
    var serverOptions = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
    serverOptions.Listen(IPAddress.Loopback, 0, listen =>
    {
        listen.Protocols = process.Type == ProcessType.Gate ? HttpProtocols.Http1 : HttpProtocols.Http2;
    });

    log.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "app 监听配置完成");
    if (process.Type != ProcessType.Gate)
    {
        app.MapGrpcService<LinkService>();
        log.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "gRPC 服务映射完成");
    }

    await app.StartAsync();
    var address = app.Urls.Single();
    log.Info(ServerLogEvents.Listen, process.Role, detail: address);
    log.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "app 初始化完成");
}

static async Task WatchZooKeeperSessionAsync(ZooKeeperProxy zooKeeper, CancellationToken stop)
{
    await zooKeeper.SessionLost.WaitAsync(stop);
    throw new InvalidOperationException("The process lost its ZooKeeper session.");
}

static async Task WatchStopAsync(string runDir, string processName, string identityKey, CancellationTokenSource lifetime)
{
    var stopGroup = Path.Combine(runDir, ServerRunFiles.StopGroup);
    var stopRole = Path.Combine(runDir, ServerRunFiles.StopRolePrefix + processName);
    var stopProcess = Path.Combine(runDir, ServerRunFiles.StopProcessPrefix + identityKey);
    while (!File.Exists(stopGroup) && !File.Exists(stopRole) && !File.Exists(stopProcess))
    {
        await Task.Delay(StopPollIntervalMilliseconds, lifetime.Token);
    }

    lifetime.Cancel();
}

static async Task SuperviseAsync(Task worker, CancellationTokenSource lifetime)
{
    try
    {
        await worker;
    }
    catch (Exception) when (lifetime.IsCancellationRequested)
    {
    }
    catch
    {
        lifetime.Cancel();
        throw;
    }
}
