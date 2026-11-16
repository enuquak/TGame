namespace TGame.Server.Engine;

internal sealed class SnowflakeSequence
{
    internal const int WorkerBits = 10;
    internal const int SequenceBits = 12;
    internal const int TimestampBits = 41;
    internal const int MaxWorkerId = (1 << WorkerBits) - 1;
    internal const int MaxSequence = (1 << SequenceBits) - 1;
    internal const long MaxTimestamp = (1L << TimestampBits) - 1;
    internal static readonly long EpochMilliseconds = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    private readonly object sync = new();
    private readonly long workerComponent;
    private long lastTimestamp = -1;
    private int sequence;

    internal SnowflakeSequence(int workerId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(workerId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(workerId, MaxWorkerId);
        workerComponent = (long)workerId << SequenceBits;
    }

    internal long? TryGenId(long timestamp)
    {
        lock (sync)
        {
            if (timestamp < 0 || timestamp > MaxTimestamp)
            {
                throw new InvalidOperationException("The clock is outside the Snowflake timestamp range.");
            }

            if (timestamp < lastTimestamp)
            {
                throw new InvalidOperationException("The clock moved backwards; ID generation is suspended until it catches up.");
            }

            if (timestamp == lastTimestamp)
            {
                if (sequence == MaxSequence)
                {
                    return null;
                }

                sequence++;
            }
            else
            {
                sequence = 0;
            }

            lastTimestamp = timestamp;
            long timestampComponent = timestamp << (WorkerBits + SequenceBits);
            return timestampComponent | workerComponent | (uint)sequence;
        }
    }
}
