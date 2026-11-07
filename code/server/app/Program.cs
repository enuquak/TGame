using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using TGame.PingPong;
using TGame.Server.Logic;

if (args is ["client", var directory, var endpoint, var pin])
{
    await ProbeClient.RunAsync(directory, new Uri(endpoint), pin);
    return;
}

if (args.Length != 2 || !Topology.Dials.ContainsKey(args[0]))
    throw new ArgumentException($"Usage: TGame.PingPong <{string.Join('|', Topology.Dials.Keys)}> <run-directory>");

var role = args[0];
var runDir = Path.GetFullPath(args[1]);
var runId = Guid.ParseExact(Path.GetFileName(runDir), "N");
using var serverLog = TraceLog.OpenServer(role, runId);
try
{
    await RunServerAsync(role, runId, runDir);
}
catch (Exception error)
{
    TraceLog.Event("PROCESS_FAILED", role, detail: error.ToString());
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

static async Task RunServerAsync(string role, Guid runId, string runDir)
{
    var singlePoints = new SinglePointRegistry(typeof(ServerSinglePoint).Assembly.GetTypes());
    var processType = Enum.Parse<ProcessType>(role);
    var singlePoint = singlePoints.Create(processType);
    using var resources = singlePoint as IDisposable;
    var node = new ProbeNode(role, singlePoint);

    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    using var certificate = processType == ProcessType.Gate ? FindDevelopmentCertificate() : null;
    builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
    {
        listen.Protocols = certificate is not null || singlePoint is GMServerSinglePoint ? HttpProtocols.Http1 : HttpProtocols.Http2;
        if (certificate is not null) listen.UseHttps(certificate);
    }));
    builder.Services.AddGrpc();
    builder.Services.AddSingleton(node);

    await using var app = builder.Build();
    if (processType == ProcessType.Gate)
    {
        app.UseWebSockets();
        app.MapGet("/probe", node.WebSocketAsync);
    }
    else if (singlePoint is GMServerSinglePoint gm)
        GMHttp.Map(app, node, gm);
    else
        app.MapGrpcService<LinkService>();

    await app.StartAsync();
    var address = app.Urls.Single();
    TraceLog.Event("LISTEN", role, detail: address, link: certificate?.GetCertHashString(HashAlgorithmName.SHA256));

    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
    var stop = lifetime.Token;
    bool consoleMode = File.Exists(Path.Combine(runDir, "console"));

    try
    {
        var initialization = node.InitializeAsync(stop);
        await using var registry = new ProbeRegistry(runId);
        using var registrationTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        registrationTimeout.CancelAfter(TimeSpan.FromSeconds(40));
        var registration = registry.RegisterAsync(role, address, consoleMode, registrationTimeout.Token);

        var workers = Topology.Dials[role].Select(peer => SuperviseAsync(ConnectAsync(peer))).ToList();
        workers.Add(SuperviseAsync(initialization));
        workers.Add(SuperviseAsync(registration));
        if (!consoleMode)
            workers.Add(SuperviseAsync(node.FirstLinks.WaitAsync(TimeSpan.FromSeconds(40), stop)));

        workers.Add(SuperviseAsync(WatchStopAsync()));
        await Task.WhenAll(workers);

        async Task ConnectAsync(string peer)
        {
            await registration;
            await node.MaintainLinkAsync(peer, registry, consoleMode, stop);
        }
    }
    finally
    {
        lifetime.Cancel();
        await app.StopAsync(TimeSpan.FromSeconds(3));
    }

    async Task WatchStopAsync()
    {
        var stopGroup = Path.Combine(runDir, "stop");
        var stopRole = Path.Combine(runDir, $"stop-{role}");
        while (!File.Exists(stopGroup) && !File.Exists(stopRole))
            await Task.Delay(100, stop);

        lifetime.Cancel();
    }

    async Task SuperviseAsync(Task worker)
    {
        try
        {
            await worker;
        }
        catch (Exception) when (stop.IsCancellationRequested) { }
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
    return store.Certificates.Where(c => c.HasPrivateKey && c.NotBefore <= DateTime.Now && c.NotAfter > DateTime.Now
        && c.Extensions.Any(e => e.Oid?.Value == "1.3.6.1.4.1.311.84.1.1"))
        .OrderByDescending(c => c.NotAfter).FirstOrDefault()
        ?? throw new InvalidOperationException("Create a local development certificate with dotnet dev-certs https.");
}
