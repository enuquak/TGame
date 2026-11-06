using System.Text.Json;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;

namespace TGame.PingPong;

internal static class TraceLog
{
    private const long FileSizeLimit = 10 * 1024 * 1024;
    private static Logger? serverLog;
    private static string? runId;

    public static IDisposable OpenServer(string role, Guid run)
    {
        runId = run.ToString("N");
        var root = Environment.GetEnvironmentVariable("TGAME_SERVER_LOG_ROOT") ?? Path.Combine("logs", "server");
        var directory = Directory.CreateDirectory(Path.Combine(root, role + "Server"));
        var path = Path.Combine(directory.FullName, $"{runId}-{Environment.ProcessId}-.log");
        SelfLog.Enable(TextWriter.Synchronized(Console.Error));
        serverLog = new LoggerConfiguration().WriteTo.File(path, outputTemplate: "{Message:lj}{NewLine}",
            rollingInterval: RollingInterval.Hour, rollOnFileSizeLimit: true, fileSizeLimitBytes: FileSizeLimit,
            retainedFileCountLimit: null).CreateLogger();
        return serverLog;
    }

    public static void Event(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null)
    {
        var json = JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, ev, role, pid = Environment.ProcessId,
            runId, peer, id, entity, link, detail });
        if (serverLog is null) Console.WriteLine(json); // The headless test client uses stdout.
        else serverLog.Information("{Json:l}", json);
    }
}
