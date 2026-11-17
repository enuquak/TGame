using System.Diagnostics;
using TGame.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace TGame.Server.Engine;

public static class TraceLog
{
    private const long FileSizeLimit = 10 * 1024 * 1024;
    private static readonly LoggingLevelSwitch minimumLevel = new();
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
        serverLog = new LoggerConfiguration().MinimumLevel.ControlledBy(minimumLevel)
            .WriteTo.File(path, outputTemplate: "{Message:lj}{NewLine}",
            rollingInterval: RollingInterval.Hour, rollOnFileSizeLimit: true, fileSizeLimitBytes: FileSizeLimit,
            retainedFileCountLimit: null).CreateLogger();
        return serverLog;
    }

    public static void SetMinimumLevel(ServerLogLevel level) => minimumLevel.MinimumLevel = ToSerilogLevel(level);

    public static void Debug(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null) =>
        Write(ServerLogLevel.Debug, ev, role, peer, id, entity, link, detail);

    public static void Info(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null) =>
        Write(ServerLogLevel.Info, ev, role, peer, id, entity, link, detail);

    public static void Error(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null) =>
        Write(ServerLogLevel.Error, ev, role, peer, id, entity, link, detail);

    private static void Write(ServerLogLevel level, string ev, string role, string? peer, string? id,
        string? entity, string? link, string? detail)
    {
        var severity = ToSerilogLevel(level);
        if (severity < minimumLevel.MinimumLevel)
        {
            return;
        }

        var json = LogEventJson.Serialize(level, ev, role, peer, id, entity, link, detail, instance);

        if (serverLog is null)
        {
            Console.WriteLine(json);
            return;
        }

        serverLog.Write(severity, "{Json:l}", json);
    }

    private static LogEventLevel ToSerilogLevel(ServerLogLevel level) => level switch
    {
        ServerLogLevel.Debug => LogEventLevel.Debug,
        ServerLogLevel.Info => LogEventLevel.Information,
        ServerLogLevel.Error => LogEventLevel.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown server log level.")
    };
}
