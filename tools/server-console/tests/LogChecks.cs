using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TGame;
using TGame.ServerConsole;
using TGame.Server.Engine;
using System.Globalization;

internal static class LogChecks
{
    private const int ChildExitTimeoutMilliseconds = 3000;
    private const string ProcessName = "LogCheck";
    private const string TrafficEvent = "TRAFFIC";

    // A private executable waiting on stdin supplies a real image path, PID and creation time.
    public static void Run(string checkout)
    {
        var idGenerator = new SnowflakeIdGenerator(0, TimeProvider.System);
        var id = idGenerator.GenId().ToString(CultureInfo.InvariantCulture);
        var run = Directory.CreateDirectory(Path.Combine(checkout, "build", "log-check", id)).FullName;
        var logs = Directory.CreateDirectory(Path.Combine(run, ProcessName + "Server")).FullName;
        var exe = Path.Combine(run, ProcessName + "Server.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "sort.exe"), exe);
        using var child = Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        })!;
        try
        {
            var identity = Path.Combine(run, ProcessName + ".json");
            long ticks = child.StartTime.ToUniversalTime().Ticks;
            string prefix = Path.Combine(logs, $"{id}-{child.Id}-2026110712");

            var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string Row(string ev, string detail = "", int? pid = null, string? runId = null, long? time = null) =>
                JsonSerializer.Serialize(new
                {
                    runId = runId ?? id,
                    pid = pid ?? child.Id,
                    role = ProcessName,
                    time = new DateTimeOffset(time ?? DateTime.UtcNow.Ticks, TimeSpan.Zero),
                    ev,
                    detail
                }, options);
            void SaveIdentity(long start) => File.WriteAllText(identity, JsonSerializer.Serialize(new
            {
                Pid = child.Id,
                StartedUtcTicks = start
            }));
            SaveIdentity(ticks);
            File.WriteAllLines(prefix + ".log", [
                Row(ServerLogEvents.ProcessFailed, time: ticks - 1), Row(ServerLogEvents.ProcessFailed, pid: 1),
                Row(ServerLogEvents.ProcessFailed, runId: idGenerator.GenId().ToString(CultureInfo.InvariantCulture)), Row(ServerLogEvents.SinglePointInit), Row(ServerLogEvents.LinksReady)
            ]);
            using var item = new ServerInstance(ProcessName);
            item.Open(run, run, run);
            Check(item.Alive && item.LineCount == 2 && item.Status == ServerStatus.Initializing, "validated identity and initialization");
            File.AppendAllText(prefix + ".log", Row(ServerLogEvents.SinglePointReady) + "\n{damaged line}\n");
            item.Refresh();
            Check(item.LineCount == 4 && item.Status == ServerStatus.Ready && item.Lines[^1] == "{damaged line}", "diagnostic row");

            File.WriteAllLines(prefix + "_999.log", Enumerable.Repeat(Row(TrafficEvent, new string('a', 256)), 1200));
            File.WriteAllText(prefix + "_1000.log", Row(ServerLogEvents.LinksLost) + "\n");
            item.Open(run, run, run);
            Check(item.Status == ServerStatus.Initializing, "replay cannot use a historical green light");
            for (int tick = 0; tick < 8; tick++)
            {
                item.Refresh();
            }

            Check(item.LineCount == 1205 && item.Lines.Count == ServerInstance.MaxRetainedLines && item.Status == ServerStatus.Disconnected, "numeric roll order and bounded history");

            var next = prefix.Replace("2026110712", "2026110713") + ".log";
            var ready = Row(ServerLogEvents.LinksReady, "中文");
            var bytes = Encoding.UTF8.GetBytes(ready + "\r\n");
            int split = Array.IndexOf(bytes, (byte)0xe4) + 1;
            Check(split > 1, "multibyte fixture");
            using (var writer = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                writer.Write(bytes.AsSpan(0, split));
                writer.Flush();
                item.Refresh();
                Check(item.LineCount == 1205 && item.Status == ServerStatus.Disconnected, "partial UTF-8 row");
                writer.Write(bytes.AsSpan(split));
            }

            item.Refresh();
            Check(item.Status == ServerStatus.Ready && item.Lines[^1] == ready, "completed row after hour roll");
            File.WriteAllText(next, Row(ServerLogEvents.LinksLost) + "\n");
            item.Refresh();
            Check(item.Status == ServerStatus.Disconnected, "truncated shard");
            File.AppendAllText(next, Row(ServerLogEvents.ProcessFailed) + "\n" + Row(ServerLogEvents.LinksReady) + "\n");
            item.Refresh();
            Check(item.Alive && item.Status == ServerStatus.Failed, "failure overrides connectivity");

            SaveIdentity(ticks + TimeSpan.TicksPerSecond);
            using var reused = new ServerInstance(ProcessName);
            reused.Open(run, run, run);
            Check(!reused.Alive && reused.Status == ServerStatus.Failed, "reused PID");
            SaveIdentity(ticks);
            using var otherPath = new ServerInstance(ProcessName);
            otherPath.Open(run, run, Path.Combine(run, "other"));
            Check(!otherPath.Alive, "unrelated apphost path");
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(ChildExitTimeoutMilliseconds))
            {
                child.Kill();
                child.WaitForExit();
            }
        }

        Console.WriteLine("PASS rolling logs, partial UTF-8, readiness, replay and PID ownership.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Log check failed: " + name);
        }
    }
}
