using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using TGame;
using TGame.Server;
using TGame.Server.Engine;
using TGame.Server.Logic;

const int ProcessArgumentCount = 2;
const int RegistrationTimeoutSeconds = 40;
const int ShutdownTimeoutSeconds = 3;
const int StopPollIntervalMilliseconds = 100;
const string DevelopmentCertificateOid = "1.3.6.1.4.1.311.84.1.1";

if (args.Length != ProcessArgumentCount)
{
    throw new ArgumentException("Usage: TGame.Server <instance-name> <run-directory>");
}

var runDir = Path.GetFullPath(args[1]);
using var configuration = ServerConfiguration.Initialize(Path.Combine(runDir, ServerGroup.FileName));
var process = ServerConfiguration.GetProcess(args[0]);
var role = process.Role;
using var serverLog = GameLog.OpenServer(process);
try
{
    await RunServerAsync(process, runDir);
}
catch (Exception error)
{
    GameLog.Error(ServerLogEvents.ProcessFailed, role, detail: error.ToString(), bothPrintConsole: true);
    Environment.ExitCode = 1;
}

static async Task RunServerAsync(ServerProcess process, string runDir)
{
    var settings = LoadConfiguration();
    GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: $"{nameof(GameLog)} 日志初始化完成");
    GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "进程组配置初始化完成");
    GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: $"{nameof(ServerSettings)} 服务端配置加载完成");

    await using var zooKeeperLifetime = await InitializeZooKeeperAsync(settings, process.Role);
    GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: $"{nameof(ZooKeeperProxy)} 单例初始化完成");

    await using var idLifetime = await InitializeIdGeneratorAsync(process.Role);
    GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: $"{nameof(IdGenerator)} 单例初始化完成");

    using var node = new ServerNode(process.Name);
    GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: $"{nameof(ServerNode)} 节点创建完成");

    await using var app = await InitializeApplicationAsync(process, node);

    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    var stop = lifetime.Token;
    var workers = new List<Task>();
    try
    {
        workers.Add(SuperviseAsync(WatchStopAsync(runDir, process.Name, lifetime), lifetime));
        GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "停止信号监控已启动");

        workers.Add(SuperviseAsync(WatchZooKeeperSessionAsync(stop), lifetime));
        GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "ZooKeeper 会话监控已启动");

        await InitializeEngineAsync();
        await InitializeSinglePointAsync();
        GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "进程初始化完成");

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

    async Task InitializeEngineAsync()
    {
        using (var registrationTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            registrationTimeout.CancelAfter(TimeSpan.FromSeconds(RegistrationTimeoutSeconds));
            await ZooKeeperProxy.RegisterProcessAsync(process, app.Urls.Single(), registrationTimeout.Token);
        }

        foreach (var peer in Topology.DialTargets(process.Name))
        {
            workers.Add(SuperviseAsync(node.MaintainLinkAsync(peer, stop), lifetime));
            GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, peer, detail: "连接维护任务已启动");
        }

        await node.FirstLinks.WaitAsync(stop);
        GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "引擎层初始化完成");
    }

    async Task InitializeSinglePointAsync()
    {
        var singlePoints = new SinglePointRegistry(typeof(ProfileServerSinglePoint).Assembly.GetTypes());
        GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: $"{nameof(SinglePointRegistry)} 类型映射加载完成");

        await node.InitializeSinglePointAsync(singlePoints, stop);
    }
}

static ServerSettings LoadConfiguration()
{
    var settings = ServerSettings.Current.Value;
    GameLog.SetMinimumLevel(settings.MinimumLogLevel);
    return settings;
}

static Task<ZooKeeperProxy> InitializeZooKeeperAsync(ServerSettings settings, string role)
{
    var configPath = Path.Combine(AppContext.BaseDirectory, "config", ZooKeeperOptions.FileName);
    var options = ZooKeeperOptions.Load(configPath, settings.ZooKeeper, settings.DiscoveryRoot);
    GameLog.Info(ServerLogEvents.InitStepCompleted, role, detail: $"{nameof(ZooKeeperOptions)} 配置加载完成");

    return ZooKeeperProxy.InitializeAsync(options);
}

static Task<IdGenerator> InitializeIdGeneratorAsync(string role)
{
    var idConfigPath = Path.Combine(AppContext.BaseDirectory, "config", IdGeneratorOptions.FileName);
    var idOptions = IdGeneratorOptions.Load(idConfigPath);
    GameLog.Info(ServerLogEvents.InitStepCompleted, role, detail: $"{nameof(IdGeneratorOptions)} 配置加载完成");

    return IdGenerator.InitializeAsync(idOptions);
}

static async Task<WebApplication> InitializeApplicationAsync(ServerProcess process, ServerNode node)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    if (process.Type == ProcessType.Gate)
    {
        builder.Services.AddSingleton<X509Certificate2>(_ => FindDevelopmentCertificate());
    }

    builder.Services.AddGrpc();
    builder.Services.AddSingleton<ServerNode>(node);
    GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "app 服务注册完成");

    var app = builder.Build();
    try
    {
        GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "app 构建完成");

        // 构建应用后再创建证书，初始化失败也由 app 统一释放。
        var certificate = process.Type == ProcessType.Gate ? app.Services.GetRequiredService<X509Certificate2>() : null;
        if (certificate is not null)
        {
            GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "HTTPS 证书加载完成");
        }

        var serverOptions = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        serverOptions.Listen(IPAddress.Loopback, 0, listen =>
        {
            if (certificate is not null)
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            }
            else
            {
                listen.Protocols = HttpProtocols.Http2;
            }
        });

        GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "app 监听配置完成");

        if (process.Type != ProcessType.Gate)
        {
            app.MapGrpcService<LinkService>();
            GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "gRPC 服务映射完成");
        }

        await app.StartAsync();
        var address = app.Urls.Single();
        GameLog.Info(ServerLogEvents.Listen, process.Role, detail: address, link: certificate?.GetCertHashString(HashAlgorithmName.SHA256));
        GameLog.Info(ServerLogEvents.InitStepCompleted, process.Role, detail: "app 初始化完成");
        return app;
    }
    catch
    {
        await app.DisposeAsync();
        throw;
    }
}

static async Task WatchZooKeeperSessionAsync(CancellationToken stop)
{
    await ZooKeeperProxy.SessionLost.WaitAsync(stop);
    throw new InvalidOperationException("The process lost its ZooKeeper session.");
}

static async Task WatchStopAsync(string runDir, string processName, CancellationTokenSource lifetime)
{
    var stopGroup = Path.Combine(runDir, ServerRunFiles.StopGroup);
    var stopRole = Path.Combine(runDir, ServerRunFiles.StopRolePrefix + processName);
    while (!File.Exists(stopGroup) && !File.Exists(stopRole))
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

static X509Certificate2 FindDevelopmentCertificate()
{
    using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
    store.Open(OpenFlags.ReadOnly);

    var candidates = new List<X509Certificate2>();
    foreach (var certificate in store.Certificates)
    {
        if (!certificate.HasPrivateKey)
        {
            continue;
        }

        if (certificate.NotBefore > DateTime.Now)
        {
            continue;
        }

        if (certificate.NotAfter <= DateTime.Now)
        {
            continue;
        }

        bool isDevelopmentCertificate = certificate.Extensions.Any(extension => extension.Oid?.Value == DevelopmentCertificateOid);
        if (!isDevelopmentCertificate)
        {
            continue;
        }

        candidates.Add(certificate);
    }

    var certificatesByExpiry = candidates.OrderByDescending(certificate => certificate.NotAfter);
    var selectedCertificate = certificatesByExpiry.FirstOrDefault();
    if (selectedCertificate is null)
    {
        throw new InvalidOperationException("Create a local development certificate with dotnet dev-certs https.");
    }

    return selectedCertificate;
}
