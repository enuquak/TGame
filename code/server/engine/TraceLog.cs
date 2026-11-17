using System.Diagnostics;
using TGame.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;

namespace TGame.Server.Engine;

public static class TraceLog
{
    private const long FileSizeLimit = 10 * 1024 * 1024;
    private static Logger? serverLog;

    private static string? instance;

    public static IDisposable OpenServer(ServerProcess server)
    {
        instance = server.Name;
        using var process = Process.GetCurrentProcess();
        long startedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        var root = Environment.GetEnvironmentVariable(EngineEnvironment.LogRoot) ?? Path.Combine("logs", "server");
        var directory = Directory.CreateDirectory(Path.Combine(root, server.Name + "Server"));
        var path = Path.Combine(directory.FullName, $"{Environment.ProcessId}-{startedUtcTicks}-.log");
        SelfLog.Enable(TextWriter.Synchronized(Console.Error));
        serverLog = new LoggerConfiguration().WriteTo.File(path, outputTemplate: "{Message:lj}{NewLine}",
            rollingInterval: RollingInterval.Hour, rollOnFileSizeLimit: true, fileSizeLimitBytes: FileSizeLimit,
            retainedFileCountLimit: null).CreateLogger();
        return serverLog;
    }

    public static void Event(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null)
    {
        var json = LogEventJson.Serialize(ev, role, peer, id, entity, link, detail, instance);

        if (serverLog is null)
        {
            Console.WriteLine(json);
            return;
        }

        serverLog.Information("{Json:l}", json);
    }
}
