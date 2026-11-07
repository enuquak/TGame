using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TGame.ServerConsole;

internal static class LogChecks
{
    // A private executable waiting on stdin supplies a real image path, PID and creation time.
    public static void Run(string checkout)
    {
        var id = Guid.NewGuid().ToString("N");
        var run = Directory.CreateDirectory(Path.Combine(checkout, "build", "log-check", id)).FullName;
        var logs = Directory.CreateDirectory(Path.Combine(run, "LogCheckServer")).FullName;
        var exe = Path.Combine(run, "LogCheckServer.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "sort.exe"), exe);
        using var child = Process.Start(new ProcessStartInfo(exe)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            var identity = Path.Combine(run, "LogCheck.json");
            long ticks = child.StartTime.ToUniversalTime().Ticks;
            string prefix = Path.Combine(logs, $"{id}-{child.Id}-2026110712");
            var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string Row(string ev, string detail = "", int? pid = null, string? runId = null, long? time = null) =>
                JsonSerializer.Serialize(new { runId = runId ?? id, pid = pid ?? child.Id, role = "LogCheck",
                    time = new DateTimeOffset(time ?? DateTime.UtcNow.Ticks, TimeSpan.Zero), ev, detail }, options);
            void SaveIdentity(long start) => File.WriteAllText(identity, JsonSerializer.Serialize(new { Pid = child.Id, StartedUtcTicks = start }));
            SaveIdentity(ticks);
            File.WriteAllLines(prefix + ".log", [
                Row("PROCESS_FAILED", time: ticks - 1), Row("PROCESS_FAILED", pid: 1),
                Row("PROCESS_FAILED", runId: Guid.NewGuid().ToString("N")), Row("SINGLE_POINT_INIT"), Row("LINKS_READY")
            ]);
            using var item = new ServerInstance("LogCheck");
            item.Open(run, run, run);
            Check(item.Alive && item.LineCount == 2 && item.Status == "初始化中", "validated identity and initialization");
            File.AppendAllText(prefix + ".log", Row("SINGLE_POINT_READY") + "\n{damaged line}\n");
            item.Refresh();
            Check(item.LineCount == 4 && item.Status == "就绪" && item.Lines[^1] == "{damaged line}", "diagnostic row");

            File.WriteAllLines(prefix + "_999.log", Enumerable.Repeat(Row("TRAFFIC", new string('a', 256)), 1200));
            File.WriteAllText(prefix + "_1000.log", Row("LINKS_LOST") + "\n");
            item.Open(run, run, run);
            Check(item.Status == "初始化中", "replay cannot use a historical green light");
            for (int tick = 0; tick < 8; tick++) item.Refresh();
            Check(item.LineCount == 1205 && item.Lines.Count == 1000 && item.Status == "连接不全", "numeric roll order and bounded history");

            var next = prefix.Replace("2026110712", "2026110713") + ".log";
            var ready = Row("LINKS_READY", "中文");
            var bytes = Encoding.UTF8.GetBytes(ready + "\r\n");
            int split = Array.IndexOf(bytes, (byte)0xe4) + 1;
            Check(split > 1, "multibyte fixture");
            using (var writer = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                writer.Write(bytes.AsSpan(0, split));
                writer.Flush();
                item.Refresh();
                Check(item.LineCount == 1205 && item.Status == "连接不全", "partial UTF-8 row");
                writer.Write(bytes.AsSpan(split));
            }
            item.Refresh();
            Check(item.Status == "就绪" && item.Lines[^1] == ready, "completed row after hour roll");
            File.WriteAllText(next, Row("LINKS_LOST") + "\n");
            item.Refresh();
            Check(item.Status == "连接不全", "truncated shard");
            File.AppendAllText(next, Row("PROCESS_FAILED") + "\n" + Row("LINKS_READY") + "\n");
            item.Refresh();
            Check(item.Alive && item.Status == "异常退出", "failure overrides connectivity");

            SaveIdentity(ticks + TimeSpan.TicksPerSecond);
            using var reused = new ServerInstance("LogCheck");
            reused.Open(run, run, run);
            Check(!reused.Alive && reused.Status == "异常退出", "reused PID");
            SaveIdentity(ticks);
            using var otherPath = new ServerInstance("LogCheck");
            otherPath.Open(run, run, Path.Combine(run, "other"));
            Check(!otherPath.Alive, "unrelated apphost path");
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(); }
        }
        Console.WriteLine("PASS rolling logs, partial UTF-8, readiness, replay and PID ownership.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Log check failed: " + name);
    }
}
