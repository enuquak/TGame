using TGame.Server.Engine;
using System.Net.WebSockets;
using Google.Protobuf;
using TGame.Diagnostics;
using TGame.Profile;
using TGame.Transport;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode
{
    private const string ProbePath = "/probe";

    public void MapDiagnostics(WebApplication app)
    {
        if (!diagnosticsEnabled)
        {
            return;
        }

        app.UseWebSockets();
        app.MapGet(ProbePath, WebSocketAsync);
    }

    internal async Task<Pong> RouteAsync(string from, Ping ping, CancellationToken stop)
    {
        var fromRole = PeerRole(from);
        if (!Ready)
        {
            return Unavailable(ping);
        }

        var gateLocal = ping.Entry == nameof(ProcessType.Gate) && Role == nameof(ProcessType.Gate) && ping.EntityId == State.EntityId;
        if (!IdText.IsValid(ping.RequestId) ||
            (!gateLocal && ping.Entry is not (nameof(ProcessType.Base) or nameof(ProcessType.Login))) ||
            !IdText.IsValid(ping.EntityId) ||
            (ping.Storage && ping.Entry != nameof(ProcessType.Base)))
        {
            throw new InvalidDataException("Invalid ping.");
        }

        if (localEntities.ContainsKey(ping.EntityId))
        {
            if (ping.Storage)
            {
                if (fromRole != nameof(ProcessType.Base) || SinglePoint is not DBMgrServerSinglePoint db)
                {
                    throw new InvalidDataException("Storage probe did not use the dedicated link.");
                }

                await db.CheckDatabaseConnectionAsync(stop);
                TraceLog.Event(ServerLogEvents.MongoPong, Role, ServerPeerNames.MongoDB, ping.RequestId, ping.EntityId);
            }

            var result = new Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Responder = Role };
            if (ping.ProfileRequest is not null)
            {
                result.ProfileReply = await ForwardDiagnosticProfileAsync(ping, stop);
            }

            return result;
        }

        string? target = Role switch
        {
            nameof(ProcessType.Gate) when fromRole == ServerPeerNames.Client => ServerProcess.FormatName(ping.Entry, 1),
            nameof(ProcessType.Base) when fromRole == nameof(ProcessType.Gate) && ping.Storage => ServerProcess.FormatName(nameof(ProcessType.DBMgr), 1),
            nameof(ProcessType.Base) or nameof(ProcessType.Login) when fromRole == nameof(ProcessType.Gate) => ServerProcess.FormatName(nameof(ProcessType.Router), 1),
            nameof(ProcessType.Router) when !ping.Storage => owners.GetValueOrDefault(ping.EntityId),
            _ => throw new InvalidDataException($"No route from {from} through {Role} to {ping.EntityId}.")
        };

        if (target == null)
        {
            return Ready
                ? new Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Status = Pong.Types.Status.NotFound }
                : Unavailable(ping);
        }

        if (target == from)
        {
            throw new InvalidDataException($"Routing loop for {ping.EntityId} through {from}.");
        }

        return await ForwardPingAsync(target, ping, stop);
    }

    private static Pong Unavailable(Ping ping) => new() { RequestId = ping.RequestId, EntityId = ping.EntityId, Status = Pong.Types.Status.Unavailable };

    private async Task<Pong> ForwardPingAsync(string peer, Ping ping, CancellationToken stop)
    {
        if (!Ready || !links.TryGetValue(peer, out var link))
        {
            return Unavailable(ping);
        }

        try
        {
            return await link.PingAsync(ping, stop);
        }
        catch (Exception error) when (!stop.IsCancellationRequested && IsDeliveryFailure(error))
        {
            TraceLog.Event(ServerLogEvents.PingDeliveryFailed, Role, peer,
                ping.RequestId, ping.EntityId, detail: error.GetType().Name);
        }

        return Unavailable(ping);
    }

    public async Task WebSocketAsync(HttpContext context)
    {
        if (!diagnosticsEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest || !Ready)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        string link = IdGenerator.GenId().ToString(System.Globalization.CultureInfo.InvariantCulture);

        while (await WebSocketFrames.ReadAsync(ws, context.RequestAborted) is { } bytes)
        {
            var ping = Ping.Parser.ParseFrom(bytes);
            TraceLog.Event(ServerLogEvents.PingRx, Role, ServerPeerNames.Client, ping.RequestId, ping.EntityId, link);

            var pong = await RouteAsync(ServerPeerNames.Client, ping, context.RequestAborted);
            await ws.SendAsync(pong.ToByteArray().AsMemory(), WebSocketMessageType.Binary, true, context.RequestAborted);
            TraceLog.Event(ServerLogEvents.PongTx, Role, ServerPeerNames.Client, pong.RequestId, pong.EntityId, link);
        }

        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
    }

    // The loopback driver may only change this run's Base SinglePoint summary.
    private Task<ProfileReply> ForwardDiagnosticProfileAsync(Ping ping, CancellationToken stop)
    {
        var request = ping.ProfileRequest;
        var subject = ProfileStore.Subject(request);
        if (request.RequestId != ping.RequestId || !IdText.IsValid(subject))
        {
            throw new InvalidDataException("Invalid diagnostic Profile request.");
        }

        if (request.OperationCase == ProfileRequest.OperationOneofCase.Fetch)
        {
            if (Role is not (nameof(ProcessType.Login) or nameof(ProcessType.Scene)))
            {
                throw new InvalidDataException("Invalid diagnostic Profile reader.");
            }

            return CallProfileAsync(request, stop);
        }

        if (request.OperationCase is not (ProfileRequest.OperationOneofCase.Publish or ProfileRequest.OperationOneofCase.Withdraw) ||
            Role != nameof(ProcessType.Base) || subject != State.EntityId || ping.EntityId != subject)
        {
            throw new InvalidDataException("Invalid diagnostic Profile owner.");
        }

        return CallProfileAsync(request, stop);
    }
}
