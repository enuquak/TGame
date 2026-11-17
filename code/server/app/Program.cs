using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
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
var group = ServerGroup.Load(Path.Combine(runDir, ServerGroup.FileName));
var process = group.GetProcess(args[0]);
var role = process.Role;
using var serverLog = GameLog.OpenServer(process);
try
{
    await RunServerAsync(process, group, runDir);
}
catch (Exception error)
{
    GameLog.Error(ServerLogEvents.ProcessFailed, role, detail: error.ToString(), bothPrintConsole: true);
    Environment.ExitCode = 1;
}

static async Task RunServerAsync(ServerProcess process, ServerGroup group, string runDir)
{
    var singlePoints = new SinglePointRegistry(typeof(ProfileServerSinglePoint).Assembly.GetTypes());
    var role = process.Role;
    var processType = process.Type;
    var settings = ServerSettings.Current.Value;
    GameLog.SetMinimumLevel(settings.MinimumLogLevel);

    var builder = WebApplication.CreateSlimBuilder();
    await using var zooKeeperLifetime = await InitializeZooKeeperAsync(settings);
    await using var idLifetime = await InitializeIdGeneratorAsync();
    using var node = new ServerNode(process.Name, group);
    builder.Logging.ClearProviders();
    using var certificate = processType == ProcessType.Gate ? FindDevelopmentCertificate() : null;
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
    {
        if (certificate is not null)
        {
            listen.Protocols = HttpProtocols.Http1;
        }
        else
        {
            listen.Protocols = HttpProtocols.Http2;
        }

        if (certificate is not null)
        {
            listen.UseHttps(certificate);
        }
    }));
    builder.Services.AddGrpc();
    builder.Services.AddSingleton<ServerNode>(node);

    await using var app = builder.Build();
    if (processType != ProcessType.Gate)
    {
        app.MapGrpcService<LinkService>();
    }

    await app.StartAsync();
    var address = app.Urls.Single();
    GameLog.Info(ServerLogEvents.Listen, role, detail: address, link: certificate?.GetCertHashString(HashAlgorithmName.SHA256));

    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    var stop = lifetime.Token;

    try
    {
        using var registrationTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        registrationTimeout.CancelAfter(TimeSpan.FromSeconds(RegistrationTimeoutSeconds));
        var registration = ZooKeeperProxy.RegisterProcessAsync(group, process, address, registrationTimeout.Token);

        var workers = new List<Task>();
        foreach (var peer in Topology.DialTargets(group, process.Name))
        {
            workers.Add(SuperviseAsync(ConnectAsync(peer)));
        }

        workers.Add(SuperviseAsync(InitializeAsync()));
        workers.Add(SuperviseAsync(WatchStopAsync()));
        workers.Add(SuperviseAsync(WatchZooKeeperSessionAsync()));
        await Task.WhenAll(workers);

        async Task InitializeAsync()
        {
            await InitializeEngineAsync();
            await node.InitializeSinglePointAsync(singlePoints, stop);
        }

        async Task InitializeEngineAsync()
        {
            await registration;
            await node.FirstLinks.WaitAsync(stop);
        }

        async Task ConnectAsync(string peer)
        {
            await registration;
            await node.MaintainLinkAsync(peer, stop);
        }
    }
    finally
    {
        lifetime.Cancel();
        await app.StopAsync(TimeSpan.FromSeconds(ShutdownTimeoutSeconds));
    }

    async Task WatchZooKeeperSessionAsync()
    {
        await ZooKeeperProxy.SessionLost.WaitAsync(stop);
        throw new InvalidOperationException("The process lost its ZooKeeper session.");
    }

    async Task WatchStopAsync()
    {
        var stopGroup = Path.Combine(runDir, ServerRunFiles.StopGroup);
        var stopRole = Path.Combine(runDir, ServerRunFiles.StopRolePrefix + process.Name);
        while (!File.Exists(stopGroup) && !File.Exists(stopRole))
        {
            await Task.Delay(StopPollIntervalMilliseconds, stop);
        }

        lifetime.Cancel();
    }

    async Task SuperviseAsync(Task worker)
    {
        try
        {
            await worker;
        }
        catch (Exception) when (stop.IsCancellationRequested)
        {
        }
        catch
        {
            lifetime.Cancel();
            throw;
        }
    }
}

static Task<ZooKeeperProxy> InitializeZooKeeperAsync(ServerSettings settings)
{
    var configPath = Path.Combine(AppContext.BaseDirectory, "config", ZooKeeperOptions.FileName);
    var options = ZooKeeperOptions.Load(configPath, settings.ZooKeeper, settings.DiscoveryRoot);
    return ZooKeeperProxy.InitializeAsync(options);
}

static Task<IdGenerator> InitializeIdGeneratorAsync()
{
    var idConfigPath = Path.Combine(AppContext.BaseDirectory, "config", IdGeneratorOptions.FileName);
    var idOptions = IdGeneratorOptions.Load(idConfigPath);
    return IdGenerator.InitializeAsync(idOptions);
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
