using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;

namespace TGame.PingPong;

internal static class GmHttp
{
    public static void Map(WebApplication app, ProbeNode node)
    {
        var secret = Environment.GetEnvironmentVariable("TGAME_GM_TOKEN");
        var actor = Environment.GetEnvironmentVariable("TGAME_GM_OPERATOR");
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32 || string.IsNullOrWhiteSpace(actor) || actor.Length > 64)
            throw new InvalidOperationException("GM requires TGAME_GM_TOKEN (at least 32 characters) and TGAME_GM_OPERATOR (1–64 characters).");
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));

        app.MapPost("/gm/commands/ping/{entityId}", async (HttpContext http, string entityId) =>
        {
            var requestId = Guid.NewGuid().ToString("N");
            http.Response.Headers["X-Request-Id"] = requestId;
            http.Response.Headers.CacheControl = "no-store";
            bool validId = Guid.TryParseExact(entityId, "N", out var id);
            var target = validId ? id.ToString("N") : null;
            var authorization = http.Request.Headers.Authorization;
            bool authenticated = authorization.Count == 1 && AuthenticationHeaderValue.TryParse(authorization[0], out var auth)
                && auth.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(auth.Parameter)
                && CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(auth.Parameter)));

            IResult Finish(int status, string? responder = null)
            {
                TraceLog.Event("GM_COMMAND", node.Role, authenticated ? actor : "unauthenticated", requestId, target,
                    detail: $"ping {status}");
                return Results.Json(new { requestId, entityId = target, responder }, statusCode: status);
            }

            if (!authenticated)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Finish(401);
            }
            if (!validId) return Finish(400);
            if (!node.Ready) return Finish(503);
            var pong = await node.GmPingAsync(new Ping { RequestId = requestId, EntityId = target, Entry = "GM" },
                app.Lifetime.ApplicationStopping);
            return pong.Status switch
            {
                Pong.Types.Status.Ok => Finish(200, pong.Responder),
                Pong.Types.Status.NotFound => Finish(404),
                _ => Finish(503)
            };
        });
    }
}

internal sealed partial class ProbeNode
{
    // Only the GM HTTP handler constructs this command; it accepts no storage or profile operations.
    internal Task<Pong> GmPingAsync(Ping ping, CancellationToken stop) => ForwardGmPingAsync("Router", ping, stop);

    private async Task<Pong> RouteGmPingAsync(string from, Ping ping, CancellationToken stop)
    {
        if (!Guid.TryParseExact(ping.RequestId, "N", out _) || !Guid.TryParseExact(ping.EntityId, "N", out _) ||
            ping.Storage || ping.ProfileProbe != Ping.Types.ProfileProbe.None ||
            (role == "Router" ? from != "GM" : from != "Router"))
            throw new InvalidDataException("Invalid GM ping or ingress.");
        if (localEntities.ContainsKey(ping.EntityId))
            return new Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Responder = role };
        if (role == "Router" && owners.TryGetValue(ping.EntityId, out var owner))
            return await ForwardGmPingAsync(owner, ping, stop);
        return new Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Status = Pong.Types.Status.NotFound };
    }

    private async Task<Pong> ForwardGmPingAsync(string peer, Ping ping, CancellationToken stop)
    {
        if (links.TryGetValue(peer, out var link))
        {
            try { return await link.PingAsync(ping, stop); }
            catch (Exception ex) when (ex is IOException or RpcException or TimeoutException)
            {
                TraceLog.Event("GM_DELIVERY_FAILED", role, peer, ping.RequestId, ping.EntityId, detail: ex.GetType().Name);
            }
        }
        return new Pong { RequestId = ping.RequestId, EntityId = ping.EntityId, Status = Pong.Types.Status.Unavailable };
    }
}
