using System.Net;
using System.Threading.Channels;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal sealed class DiscoveryLinkChecks(TestServices services)
{
    public async Task RunAsync(CancellationToken stop)
    {
        var configuration = TestNodes.LoadConfiguration();
        var first = new DiscoveryPeer(ServerProcess.FormatName(nameof(ProcessType.Router), 1), services.Ids);
        var second = new DiscoveryPeer(ServerProcess.FormatName(nameof(ProcessType.Router), 2), services.Ids);
        await using var firstApp = await StartAsync(first, stop);
        await using var secondApp = await StartAsync(second, stop);
        var router1 = new DiscoveredProcess(new ServerProcess(ProcessType.Router, 1), firstApp.Urls.Single(), 1);
        var router2 = new DiscoveredProcess(new ServerProcess(ProcessType.Router, 2), secondApp.Urls.Single(), 2);
        var changes = Channel.CreateUnbounded<IReadOnlyList<DiscoveredProcess>>();
        var node = services.CreateNode(new ServerProcess(ProcessType.Scene, 1), configuration);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var maintenance = node.MaintainConnectionsAsync(changes.Reader.ReadAllAsync(lifetime.Token), lifetime.Token);
        try
        {
            changes.Writer.TryWrite([router1]);
            var old = await first.Connections.Reader.ReadAsync(stop);
            await node.FirstLinks.WaitAsync(stop);
            changes.Writer.TryWrite([router1, router2]);
            var extra = await second.Connections.Reader.ReadAsync(stop);
            await extra.Handshaken.Task.WaitAsync(stop);
            if (!node.Ready)
            {
                throw new Exception("Adding an extra Router revoked readiness.");
            }

            changes.Writer.TryWrite([router2]);
            await old.Closed.Task.WaitAsync(stop);
            if (!node.Ready)
            {
                throw new Exception("Removing a redundant Router revoked readiness.");
            }

            changes.Writer.TryWrite([router2 with { Generation = 3 }]);
            await extra.Closed.Task.WaitAsync(stop);
            var replacement = await second.Connections.Reader.ReadAsync(stop);
            await replacement.Handshaken.Task.WaitAsync(stop);
            changes.Writer.TryWrite([]);
            await replacement.Closed.Task.WaitAsync(stop);
            while (node.Ready)
            {
                await Task.Delay(1, stop);
            }

            changes.Writer.TryWrite([router1]);
            var restored = await first.Connections.Reader.ReadAsync(stop);
            await restored.Handshaken.Task.WaitAsync(stop);
            while (!node.Ready)
            {
                await Task.Delay(1, stop);
            }

            changes.Writer.TryComplete(new InvalidDataException("Discovery failed."));
            try
            {
                await maintenance;
                throw new Exception("Discovery failure was hidden.");
            }
            catch (InvalidDataException)
            {
            }

            if (node.Ready)
            {
                throw new Exception("Discovery ended before its active connections were cleaned up.");
            }

            Console.WriteLine("PASS real gRPC dynamic joins, redundant removal, replacement, recovery and failure cleanup.");
        }
        finally
        {
            lifetime.Cancel();
            try
            {
                await maintenance;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (InvalidDataException) when (changes.Reader.Completion.IsFaulted)
            {
            }
        }
    }

    private static async Task<WebApplication> StartAsync(DiscoveryPeer peer, CancellationToken stop)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(peer);
        var app = builder.Build();
        app.MapGrpcService<DiscoveryPeer>();
        await app.StartAsync(stop);
        return app;
    }
}

internal sealed class DiscoveryPeer(string name, IdGenerator ids) : ServerLink.ServerLinkBase
{
    internal sealed class Connection
    {
        internal TaskCompletionSource Handshaken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal Channel<Connection> Connections { get; } = Channel.CreateUnbounded<Connection>();

    public override async Task Connect(IAsyncStreamReader<Frame> input, IServerStreamWriter<Frame> output, ServerCallContext context)
    {
        var connection = new Connection();
        try
        {
            if (!await input.MoveNext(context.CancellationToken) || input.Current.Hello != ServerProcess.FormatName(nameof(ProcessType.Scene), 1))
            {
                throw new InvalidDataException("Unexpected caller identity.");
            }

            await output.WriteAsync(new Frame { Hello = name }, context.CancellationToken);
            var requestId = ids.GenId();
            await output.WriteAsync(RpcFrames.Request(new Empty(), requestId), context.CancellationToken);
            Connections.Writer.TryWrite(connection);
            await foreach (var frame in input.ReadAllAsync(context.CancellationToken))
            {
                if (frame.Reply?.RequestId != requestId ||
                    frame.Reply.Error?.StatusCode != (int)StatusCode.Unimplemented)
                {
                    throw new InvalidDataException("Expected an unimplemented reply after the process handshake.");
                }

                connection.Handshaken.TrySetResult();
            }
        }
        finally
        {
            connection.Closed.TrySetResult();
        }
    }
}
