using System.Net.WebSockets;
using Google.Protobuf;
using TGame.Diagnostics;
using TGame.Profile;
using TGame.Server.Logic;
using TGame.Transport;

namespace TGame.Server;

internal sealed partial class ServerNode
{
    private const string ProbePath = "/probe";

    public void MapDiagnostics(WebApplication app)
    {
        app.UseWebSockets();
        app.MapGet(ProbePath, WebSocketAsync);
    }

    private async Task<Pong> RouteAsync(string from, Ping ping, CancellationToken stop)
    {
        if (!Ready)
        {
            return Unavailable(ping);
        }

        if (ping.Entry == nameof(ProcessType.GM))
        {
            return await RouteGmPingAsync(from, ping, stop);
        }

        var gateLocal = ping.Entry == nameof(ProcessType.Gate) && role == nameof(ProcessType.Gate) && ping.EntityId == singlePoint.State.EntityId;
        if (!Guid.TryParseExact(ping.RequestId, "N", out _) || (!gateLocal && ping.Entry is not (nameof(ProcessType.Base) or nameof(ProcessType.Login)))
            || !Guid.TryParseExact(ping.EntityId, "N", out _)
            || (ping.Storage && ping.Entry != nameof(ProcessType.Base)))
        {
            throw new InvalidDataException("Invalid ping.");
        }

        if (localEntities.ContainsKey(ping.EntityId))
        {
            if (ping.Storage)
            {
                if (from != nameof(ProcessType.Base) || singlePoint is not DBMgrServerSinglePoint db)
                {
                    throw new InvalidDataException("Storage probe did not use the dedicated link.");
                }

                await db.PingAsync(stop);
                TraceLog.Event(ServerLogEvents.MongoPong, role, ServerPeerNames.MongoDB, ping.RequestId, ping.EntityId);
            }

            var result = new Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Responder = role };
            if (ping.ProfileProbe != Ping.Types.ProfileProbe.None)
            {
                result.ProfileReply = await RunProfileProbeAsync(ping, stop);
            }

            return result;
        }

        string? target = role switch
        {
            nameof(ProcessType.Gate) when from == ServerPeerNames.Client => ping.Entry,
            nameof(ProcessType.Base) when from == nameof(ProcessType.Gate) && ping.Storage => nameof(ProcessType.DBMgr),
            nameof(ProcessType.Base) or nameof(ProcessType.Login) when from == nameof(ProcessType.Gate) => nameof(ProcessType.Router),
            nameof(ProcessType.Router) when !ping.Storage => owners.GetValueOrDefault(ping.EntityId),
            _ => throw new InvalidDataException($"No route from {from} through {role} to {ping.EntityId}.")
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
            TraceLog.Event(ping.Entry == nameof(ProcessType.GM) ? ServerLogEvents.GMDeliveryFailed : ServerLogEvents.PingDeliveryFailed, role, peer,
                ping.RequestId, ping.EntityId, detail: error.GetType().Name);
        }

        return Unavailable(ping);
    }

    public async Task WebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest || !Ready)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        string link = context.Connection.Id;

        while (await WebSocketFrames.ReadAsync(ws, context.RequestAborted) is { } bytes)
        {
            var ping = Ping.Parser.ParseFrom(bytes);
            TraceLog.Event(ServerLogEvents.PingRx, role, ServerPeerNames.Client, ping.RequestId, ping.EntityId, link);

            var pong = await RouteAsync(ServerPeerNames.Client, ping, context.RequestAborted);
            await ws.SendAsync(pong.ToByteArray().AsMemory(), WebSocketMessageType.Binary, true, context.RequestAborted);
            TraceLog.Event(ServerLogEvents.PongTx, role, ServerPeerNames.Client, pong.RequestId, pong.EntityId, link);
        }

        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, context.RequestAborted);
    }

    // The loopback driver may only change this run's Base SinglePoint summary.
    private Task<ProfileReply> RunProfileProbeAsync(Ping ping, CancellationToken stop)
    {
        var request = new ProfileRequest { RequestId = ping.RequestId };
        bool ownProbe = role == nameof(ProcessType.Base) && ping.EntityId == singlePoint.State.EntityId && ping.ProfileSubject == ping.EntityId;

        switch (ping.ProfileProbe)
        {
            case Ping.Types.ProfileProbe.Publish when ownProbe:
                request.Publish = new EntitySummary { EntityId = ping.EntityId, DisplayName = ProfileProbeData.DisplayName, Title = ProfileProbeData.Title };
                break;

            case Ping.Types.ProfileProbe.Withdraw when ownProbe:
                request.Withdraw = ping.EntityId;
                break;

            case Ping.Types.ProfileProbe.Fetch when role is nameof(ProcessType.Login) or nameof(ProcessType.Scene) && Guid.TryParseExact(ping.ProfileSubject, "N", out _):
                request.Fetch = ping.ProfileSubject;
                break;

            default:
                throw new InvalidDataException("Invalid Profile probe.");
        }

        return CallProfileAsync(request, stop);
    }
}
