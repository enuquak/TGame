using TGame.Management;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using TGame.Server.Logic;

namespace TGame.Server;

internal static class GMHttp
{
    private const string PingRoute = "/gm/commands/ping/{entityId}";
    private const string RequestIdHeader = "X-Request-Id";
    private const string AuthenticationScheme = "Bearer";
    private const string NoStoreCacheControl = "no-store";
    private const string UnauthenticatedActor = "unauthenticated";

    public static void Map(WebApplication app, ServerNode node, GMServerSinglePoint gm)
    {
        app.MapPost(PingRoute, async (HttpContext http, string entityId) =>
        {
            var requestId = Guid.NewGuid().ToString("N");
            http.Response.Headers[RequestIdHeader] = requestId;
            http.Response.Headers.CacheControl = NoStoreCacheControl;

            bool validId = Guid.TryParseExact(entityId, "N", out var id);
            var target = validId ? id.ToString("N") : null;

            var authorization = http.Request.Headers.Authorization;
            bool authenticated = gm.TokenHash is { } tokenHash && authorization.Count == 1 && AuthenticationHeaderValue.TryParse(authorization[0], out var auth)
                && auth.Scheme.Equals(AuthenticationScheme, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(auth.Parameter)
                && CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(auth.Parameter)));

            IResult Finish(int status, string? responder = null)
            {
                TraceLog.Event(ServerLogEvents.GMCommand, node.Role, authenticated ? gm.Actor : UnauthenticatedActor, requestId, target,
                    detail: $"ping {status}");
                return Results.Json(new
                {
                    requestId,
                    entityId = target,
                    responder
                }, statusCode: status);
            }

            if (!authenticated)
            {
                http.Response.Headers.WWWAuthenticate = AuthenticationScheme;
                return Finish(StatusCodes.Status401Unauthorized);
            }

            if (!validId)
            {
                return Finish(StatusCodes.Status400BadRequest);
            }

            if (!node.Ready)
            {
                return Finish(StatusCodes.Status503ServiceUnavailable);
            }

            var pong = await node.GmPingAsync(new GmPingRequest { RequestId = requestId, EntityId = target },
                app.Lifetime.ApplicationStopping);

            return pong.Status switch
            {
                GmPingReply.Types.Status.Ok => Finish(StatusCodes.Status200OK, pong.Responder),
                GmPingReply.Types.Status.NotFound => Finish(StatusCodes.Status404NotFound),
                _ => Finish(StatusCodes.Status503ServiceUnavailable)
            };
        });
    }
}
