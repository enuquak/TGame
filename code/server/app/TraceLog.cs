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
        var root = Environment.GetEnvironmentVariable(ServerEnvironment.LogRoot) ?? Path.Combine("logs", "server");
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
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [ServerLogFields.Time] = DateTimeOffset.UtcNow,
            [ServerLogFields.Event] = ev,
            [ServerLogFields.Role] = role,
            [ServerLogFields.Pid] = Environment.ProcessId,
            [ServerLogFields.RunId] = runId,
            [ServerLogFields.Peer] = peer,
            [ServerLogFields.Id] = id,
            [ServerLogFields.Entity] = entity,
            [ServerLogFields.Link] = link,
            [ServerLogFields.Detail] = detail
        });

        if (serverLog is null)
        {
            Console.WriteLine(json); // The headless test client uses stdout.
            return;
        }

        serverLog.Information("{Json:l}", json);
    }
}
