using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using TGame.Server.Logic;
using Result = TGame.Profile.ProfileReply.Types.Result;

namespace TGame.PingPong;

internal static class Wire
{
    private const int MaxFrameBytes = 4096;

    public static async Task<byte[]?> ReadAsync(WebSocket ws, CancellationToken stop)
    {
        var buffer = new byte[MaxFrameBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            var chunk = await ws.ReceiveAsync(buffer.AsMemory(length), stop);
            if (chunk.MessageType == WebSocketMessageType.Close && length == 0)
            {
                return null;
            }

            if (chunk.MessageType != WebSocketMessageType.Binary)
            {
                throw new InvalidDataException("Expected binary Protobuf.");
            }

            length += chunk.Count;
            if (chunk.EndOfMessage)
            {
                return buffer[..length];
            }
        }

        throw new InvalidDataException($"Probe exceeds {MaxFrameBytes} bytes.");
    }
}

internal static class ProbeClient
{
    public const string Command = "client";
    public const string ProfileDisplayName = "TGame probe";
    public const string ProfileTitle = "Profile smoke";

    private const string CasesFileName = "cases.json";
    private const int RunTimeoutSeconds = 90;
    private const int RequestTimeoutSeconds = 15;
    private const int EntryPathIndex = 2;

    private sealed record ProbeCase(string Id, string Entity, string[] Path, bool Storage,
        string? ProfileAction, string? ProfileSubject, string? ProfileResult);

    public static async Task RunAsync(string directory, Uri endpoint, string pin)
    {
        if (!endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeWss)
        {
            throw new ArgumentException("Expected local WSS endpoint.");
        }

        var cases = JsonSerializer.Deserialize<ProbeCase[]>(await File.ReadAllTextAsync(Path.Combine(directory, CasesFileName)))
            ?? throw new InvalidDataException("Missing test cases.");

        using var ws = new ClientWebSocket();
        // Pin this run's local development certificate without modifying system trust.
        ws.Options.RemoteCertificateValidationCallback = (_, cert, _, errors) => cert != null
            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0
            && cert.GetCertHashString(HashAlgorithmName.SHA256) == pin;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(RunTimeoutSeconds));
        await ws.ConnectAsync(endpoint, stop.Token);

        foreach (var c in cases)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));

            var entry = c.Path.Length == EntryPathIndex ? nameof(ProcessType.Gate) : c.Path[EntryPathIndex];
            var ping = new Ping { RequestId = c.Id, EntityId = c.Entity, Entry = entry, Storage = c.Storage };
            if (c.ProfileAction != null)
            {
                ping.ProfileProbe = Enum.Parse<Ping.Types.ProfileProbe>(c.ProfileAction);
                ping.ProfileSubject = c.ProfileSubject;
            }

            await ws.SendAsync(ping.ToByteArray().AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            TraceLog.Event(ServerLogEvents.PingTx, ServerPeerNames.Client, nameof(ProcessType.Gate), c.Id, c.Entity);

            var bytes = await Wire.ReadAsync(ws, timeout.Token) ?? throw new IOException("Gate closed before Pong.");
            var pong = Pong.Parser.ParseFrom(bytes);
            if (pong.RequestId != c.Id || pong.EntityId != c.Entity || pong.Responder != c.Path[^1])
            {
                throw new InvalidDataException($"Pong mismatch for {c.Id}.");
            }

            if (c.ProfileAction != null)
            {
                var payload = pong.ProfileReply;
                if (payload?.RequestId != c.Id || payload.EntityId != c.ProfileSubject || payload.Result.ToString() != c.ProfileResult ||
                    (c.ProfileResult == nameof(Result.Found) && (payload.Summary?.EntityId != c.ProfileSubject ||
                       payload.Summary.DisplayName != ProfileDisplayName || payload.Summary.Title != ProfileTitle)) ||
                    (c.ProfileResult != nameof(Result.Found) && payload.Summary != null))
                {
                    throw new InvalidDataException($"Profile result mismatch for {c.Id} (expected {c.ProfileResult}, got {payload?.Result}).");
                }
            }

            TraceLog.Event(ServerLogEvents.PongRx, ServerPeerNames.Client, nameof(ProcessType.Gate), pong.RequestId, pong.EntityId, detail: pong.Responder);
        }

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, stop.Token);
    }
}
