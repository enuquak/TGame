using Serilog.Events;
using Serilog.Parsing;
using TGame.Server.Engine;

internal static class ServerLogFileChecks
{
    public static void Run(string root)
    {
        const int FileSizeLimit = 64;
        using (var sink = new ServerFileSink(root, FileSizeLimit))
        {
            sink.Emit(Row(11, 23, 5, new string('a', 80)));
            sink.Emit(Row(11, 23, 17, new string('b', 80)));
            sink.Emit(Row(11, 23, 17, "same minute"));
        }

        Check(File.ReadAllLines(Path.Combine(root, "202607112300.log")).Single() == new string('a', 80), "hourly filename");
        Check(File.ReadAllLines(Path.Combine(root, "202607112317.log")).Single() == new string('b', 80), "size roll uses actual minute");
        Check(File.ReadAllLines(Path.Combine(root, "202607112317_001.log")).Single() == "same minute", "same-minute size roll preserves both files");

        using (var reopened = new ServerFileSink(root, FileSizeLimit))
        {
            reopened.Emit(Row(11, 23, 18, "restart"));
            reopened.Emit(Row(12, 0, 3, "next hour"));
        }

        Check(File.ReadAllLines(Path.Combine(root, "202607112317_001.log")).SequenceEqual(["same minute", "restart"]), "restart appends to latest shard");
        Check(File.ReadAllLines(Path.Combine(root, "202607120000.log")).Single() == "next hour", "midnight roll uses minute zero");
        Check(Directory.GetFiles(root, "*.log").Length == 4, "rolling creates only the expected files");

        var concurrent = Path.Combine(root, "concurrent");
        using (var sink = new ServerFileSink(concurrent, 1024 * 1024))
        {
            Parallel.For(0, 64, index => sink.Emit(Row(11, 23, 5, $"row-{index}")));
        }

        var rows = File.ReadAllLines(Path.Combine(concurrent, "202607112300.log"));
        Check(rows.Length == 64 && rows.ToHashSet(StringComparer.Ordinal).SetEquals(Enumerable.Range(0, 64).Select(index => $"row-{index}")),
            "concurrent writers preserve complete lines");

        var clockRollback = Path.Combine(root, "clock-rollback");
        using (var sink = new ServerFileSink(clockRollback, FileSizeLimit))
        {
            sink.Emit(Row(11, 23, 5, new string('a', 80)));
            sink.Emit(Row(11, 23, 17, new string('b', 80)));
            sink.Emit(Row(11, 23, 10, "clock moved back"));
        }

        Check(File.ReadAllLines(Path.Combine(clockRollback, "202607112317_001.log")).Single() == "clock moved back",
            "clock rollback keeps shard order monotonic");

        var overlapping = Path.Combine(root, "overlapping");
        using (var first = new ServerFileSink(overlapping, FileSizeLimit))
        using (var second = new ServerFileSink(overlapping, FileSizeLimit))
        {
            first.Emit(Row(11, 23, 5, "previous process"));
            second.Emit(Row(11, 23, 5, "next process"));
        }

        Check(File.ReadAllLines(Path.Combine(overlapping, "202607112300.log")).Single() == "previous process" &&
            File.ReadAllLines(Path.Combine(overlapping, "202607112305.log")).Single() == "next process",
            "an occupied file is preserved during process handover");

        var fullCollision = Path.Combine(root, "full-collision");
        using (var first = new ServerFileSink(fullCollision, FileSizeLimit))
        {
            first.Emit(Row(11, 23, 0, new string('a', 80)));
            using (var second = new ServerFileSink(fullCollision, FileSizeLimit))
            {
                second.Emit(Row(11, 23, 0, new string('b', 80)));
            }

            first.Emit(Row(11, 23, 0, "after handover"));
        }

        Check(File.Exists(Path.Combine(fullCollision, "202607112300_002.log")) &&
            File.ReadAllLines(Path.Combine(fullCollision, "202607112300_001.log")).Single() == new string('b', 80),
            "size rolling skips an already-full collision shard");

        var interrupted = Directory.CreateDirectory(Path.Combine(root, "interrupted")).FullName;
        var interruptedFile = Path.Combine(interrupted, "202607112300.log");
        File.WriteAllText(interruptedFile, "unfinished previous record");
        using (var sink = new ServerFileSink(interrupted, FileSizeLimit))
        {
            sink.Emit(Row(11, 23, 5, "after restart"));
        }

        Check(File.ReadAllLines(interruptedFile).SequenceEqual(["unfinished previous record", "after restart"]),
            "restart separates an interrupted record from the next complete event");
    }

    private static LogEvent Row(int day, int hour, int minute, string message)
    {
        var timestamp = new DateTimeOffset(new DateTime(2026, 7, day, hour, minute, 0, DateTimeKind.Local));
        var template = new MessageTemplateParser().Parse("{Message:l}");
        return new LogEvent(timestamp, LogEventLevel.Information, null, template,
            [new LogEventProperty("Message", new ScalarValue(message))]);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception("Server log file check failed: " + message);
        }
    }
}
