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
var singlePoints = new SinglePointRegistry(typeof(ServerSinglePoint).Assembly.GetTypes());
using var profileStore = role == "Profile" ? new ProfileStore() : null;
var singlePoint = singlePoints.Create(Enum.Parse<ProcessType>(role));
var node = new ProbeNode(role, singlePoint, profileStore);
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
using var certificate = role == "Gate" ? FindDevelopmentCertificate() : null;
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
{
    listen.Protocols = certificate is not null || singlePoint is GMServerSinglePoint ? HttpProtocols.Http1 : HttpProtocols.Http2;
    if (certificate is not null) listen.UseHttps(certificate);
}));
builder.Services.AddGrpc();
builder.Services.AddSingleton(node);
await using var app = builder.Build();
if (role == "Gate")
{
    app.UseWebSockets();
    app.MapGet("/probe", node.WebSocketAsync);
}
else if (singlePoint is GMServerSinglePoint) GmHttp.Map(app, node);
else app.MapGrpcService<LinkService>();
await app.StartAsync();
var address = app.Urls.Single();
TraceLog.Event("LISTEN", role, detail: address, link: certificate?.GetCertHashString(HashAlgorithmName.SHA256));
using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
await using var registry = new ProbeRegistry(runId);
var peers = await registry.RegisterAndDiscoverAsync(role, address).WaitAsync(TimeSpan.FromSeconds(40));
var connections = node.Start(peers, lifetime.Token);
try
{
    while (!File.Exists(Path.Combine(runDir, "stop")) && !lifetime.IsCancellationRequested)
    {
        foreach (var task in connections.Where(task => task.IsCompleted))
        {
            await task;
            throw new IOException("A peer stream closed before shutdown.");
        }
        if (node.LinkCount == Topology.Degree(role) && !node.Ready)
        {
            node.Ready = true;
            TraceLog.Event("READY", role, detail: node.LinkCount.ToString());
        }
        await Task.Delay(100);
    }
}
finally
{
    lifetime.Cancel();
    await app.StopAsync(TimeSpan.FromSeconds(3));
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
