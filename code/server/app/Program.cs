using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using TGame;
using TGame.Server;
using TGame.Server.Engine;
using TGame.Server.Logic;

const int ProcessArgumentCount = 2;
const int StartupTimeoutSeconds = 40;
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
using var serverLog = TraceLog.OpenServer(process);
try
{
    await RunServerAsync(process, group, runDir);
}
catch (Exception error)
{
    TraceLog.Event(ServerLogEvents.ProcessFailed, role, detail: error.ToString());
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

static async Task RunServerAsync(ServerProcess process, ServerGroup group, string runDir)
{
    var singlePoints = new SinglePointRegistry(typeof(GameServerNode).Assembly.GetTypes());
    var role = process.Role;
    var processType = process.Type;
    var settings = ServerSettings.Current.Value;
    bool diagnosticsEnabled = ServerSettings.DiagnosticsEnabled();

    var builder = WebApplication.CreateSlimBuilder();
    var messages = new RpcDispatcher();
    var idOptions = IdGeneratorOptions.Load(Path.Combine(AppContext.BaseDirectory, "config", IdGeneratorOptions.FileName), settings.ZooKeeper);
    await using var idGenerator = await SnowflakeIdGenerator.CreateAsync(idOptions);
    using var node = new GameServerNode(process.Name, group, messages, idGenerator, diagnosticsEnabled);
    ServerMessages.Register(messages, node, diagnosticsEnabled);
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
    builder.Services.AddSingleton<IIdGenerator>(idGenerator);

    await using var app = builder.Build();
    if (processType == ProcessType.Gate)
    {
        node.MapDiagnostics(app);
    }
    else
    {
        app.MapGrpcService<LinkService>();
    }

    await app.StartAsync();
    var address = app.Urls.Single();
    TraceLog.Event(ServerLogEvents.Listen, role, detail: address, link: certificate?.GetCertHashString(HashAlgorithmName.SHA256));

    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    var stop = lifetime.Token;
    bool consoleMode = File.Exists(Path.Combine(runDir, ServerRunFiles.ConsoleMode));

    try
    {
        await using var registry = new ServiceRegistry(group, settings.ZooKeeper, settings.DiscoveryRoot);
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(StartupTimeoutSeconds));
        var registration = registry.RegisterAsync(process, address, startupTimeout.Token);

        var workers = new List<Task>();
        foreach (var peer in Topology.DialTargets(group, process.Name))
        {
            workers.Add(SuperviseAsync(ConnectAsync(peer)));
        }

        workers.Add(SuperviseAsync(InitializeAsync()));
        workers.Add(SuperviseAsync(WatchStopAsync()));
        workers.Add(SuperviseAsync(WatchIdLeaseAsync()));
        await Task.WhenAll(workers);

        async Task InitializeAsync()
        {
            await InitializeEngineAsync();
            await node.InitializeSinglePointAsync(singlePoints, stop);
        }

        async Task InitializeEngineAsync()
        {
            await registration;
            if (consoleMode)
            {
                await node.FirstLinks.WaitAsync(stop);
                return;
            }

            await node.FirstLinks.WaitAsync(startupTimeout.Token);
        }

        async Task ConnectAsync(string peer)
        {
            await registration;
            await node.MaintainLinkAsync(peer, registry, consoleMode, stop);
        }
    }
    finally
    {
        lifetime.Cancel();
        await app.StopAsync(TimeSpan.FromSeconds(ShutdownTimeoutSeconds));
    }

    async Task WatchIdLeaseAsync()
    {
        await idGenerator.LeaseLost.WaitAsync(stop);
        throw new InvalidOperationException("The ID generator lost its ZooKeeper session.");
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
