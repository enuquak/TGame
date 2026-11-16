using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using TGame;
using TGame.Server;
using TGame.Server.Logic;
using TGame.Transport;

internal static class DiagnosticChecks
{
    public static async Task VerifyDisabledAsync()
    {
        var node = new ServerNode(nameof(ProcessType.Gate), new GateServerSinglePoint(), ServerMessages.Create());
        await using var app = WebApplication.CreateSlimBuilder().Build();
        node.MapDiagnostics(app);
        if (((IEndpointRouteBuilder)app).DataSources.Any(source => source.Endpoints.Count > 0))
        {
            throw new Exception("Normal startup registered diagnostic routes.");
        }

        var enabled = new ServerNode(nameof(ProcessType.Gate), new GateServerSinglePoint(), ServerMessages.Create(diagnosticsEnabled: true), diagnosticsEnabled: true);
        enabled.MapDiagnostics(app);
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints);
        var routes = endpoints.OfType<RouteEndpoint>();
        if (!routes.Any(endpoint => endpoint.RoutePattern.RawText == "/probe"))
        {
            throw new Exception("Explicit diagnostic startup did not register the probe route.");
        }

        var context = new DefaultHttpContext();
        await node.WebSocketAsync(context);
        if (context.Response.StatusCode != StatusCodes.Status404NotFound)
        {
            throw new Exception("Normal servers must not expose the diagnostic WebSocket endpoint.");
        }

        if (Frame.Descriptor.File.Dependencies.Any(file => file.Name == "pingpong.proto"))
        {
            throw new Exception("Core transport must not depend on the test Ping/Pong protocol.");
        }
    }
}
