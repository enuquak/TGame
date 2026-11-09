using System.Net.WebSockets;

namespace TGame.Transport;

public static class WebSocketFrames
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

        throw new InvalidDataException($"Frame exceeds {MaxFrameBytes} bytes.");
    }
}
