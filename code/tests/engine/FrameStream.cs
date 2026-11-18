using System.Threading.Channels;
using Grpc.Core;
using TGame.Transport;

internal sealed class FrameStream : IAsyncStreamReader<Frame>, IAsyncStreamWriter<Frame>
{
    public Channel<Frame> Input { get; } = Channel.CreateUnbounded<Frame>();
    public Channel<Frame> Output { get; } = Channel.CreateUnbounded<Frame>();
    public Frame Current { get; private set; } = null!;
    public WriteOptions? WriteOptions { get; set; }
    public Task BeforeWrite { get; init; } = Task.CompletedTask;

    public Task WriteAsync(Frame frame) => WriteAsync(frame, CancellationToken.None);

    public async Task WriteAsync(Frame frame, CancellationToken stop)
    {
        await BeforeWrite.WaitAsync(stop);
        await Output.Writer.WriteAsync(frame, stop);
    }

    public async Task<bool> MoveNext(CancellationToken stop)
    {
        if (!await Input.Reader.WaitToReadAsync(stop))
        {
            return false;
        }

        Current = await Input.Reader.ReadAsync(stop);
        return true;
    }
}
