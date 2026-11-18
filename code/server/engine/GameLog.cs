using System.Diagnostics;
using TGame.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;

namespace TGame.Server.Engine;

public sealed class GameLog : IDisposable
{
    private const long FileSizeLimit = 10 * 1024 * 1024;
    private static GameLog? instance;
    private static readonly object instanceChanges = new();

    private string? processName;
    private Logger? serverLog;
    private readonly LoggingLevelSwitch minimumLevel = new();

    private GameLog()
    {
    }

    private static GameLog Instance => Volatile.Read(ref instance)
        ?? throw new InvalidOperationException("Initialize the process logger before use.");

    public static GameLog Initialize()
    {
        lock (instanceChanges)
        {
            if (instance is not null)
            {
                throw new InvalidOperationException("The process logger is already initialized.");
            }

            var initialized = new GameLog();
            Volatile.Write(ref instance, initialized);
            return initialized;
        }
    }

    public static void OpenServer(ServerProcess server)
    {
        lock (instanceChanges)
        {
            Instance.OpenServerCore(server);
        }
    }

    private void OpenServerCore(ServerProcess server)
    {
        if (serverLog is not null)
        {
            throw new InvalidOperationException("The server log file is already open.");
        }

        using var process = Process.GetCurrentProcess();
        long startedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        var root = Environment.GetEnvironmentVariable(EngineEnvironment.LogRoot) ?? Path.Combine("logs", "server");
        var directory = Directory.CreateDirectory(Path.Combine(root, server.Name + "Server"));
        var path = Path.Combine(directory.FullName, $"{Environment.ProcessId}-{startedUtcTicks}-.log");
        SelfLog.Enable(TextWriter.Synchronized(Console.Error));
        var logger = new LoggerConfiguration().MinimumLevel.ControlledBy(minimumLevel)
            .WriteTo.File(path, outputTemplate: "{Message:lj}{NewLine}",
            rollingInterval: RollingInterval.Hour, rollOnFileSizeLimit: true, fileSizeLimitBytes: FileSizeLimit,
            retainedFileCountLimit: null).CreateLogger();
        processName = server.Name;
        Volatile.Write(ref serverLog, logger);
    }

    public void Dispose()
    {
        lock (instanceChanges)
        {
            if (!ReferenceEquals(instance, this))
            {
                return;
            }

            try
            {
                serverLog?.Dispose();
            }
            finally
            {
                SelfLog.Disable();
                Volatile.Write(ref instance, null);
            }
        }
    }

    public static void SetMinimumLevel(ServerLogLevel level) => Instance.minimumLevel.MinimumLevel = ToSerilogLevel(level);

    public static void Debug(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null) =>
        Instance.Write(ServerLogLevel.Debug, ev, role, peer, id, entity, link, detail);

    public static void Info(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null) =>
        Instance.Write(ServerLogLevel.Info, ev, role, peer, id, entity, link, detail);

    public static void Error(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null, bool bothPrintConsole = false) =>
        Instance.Write(ServerLogLevel.Error, ev, role, peer, id, entity, link, detail, bothPrintConsole);

    private void Write(ServerLogLevel level, string ev, string role, string? peer, string? id,
        string? entity, string? link, string? detail, bool bothPrintConsole = false)
    {
        var severity = ToSerilogLevel(level);
        if (severity < minimumLevel.MinimumLevel)
        {
            return;
        }

        var logger = Volatile.Read(ref serverLog);
        var json = LogEventJson.Serialize(level, ev, role, peer, id, entity, link, detail, processName);

        if (logger is not null)
        {
            logger.Write(severity, "{Json:l}", json);
        }
        else if (!bothPrintConsole)
        {
            Console.WriteLine(json);
        }

        if (bothPrintConsole)
        {
            Console.Error.WriteLine(json);
        }
    }

    private static LogEventLevel ToSerilogLevel(ServerLogLevel level) => level switch
    {
        ServerLogLevel.Debug => LogEventLevel.Debug,
        ServerLogLevel.Info => LogEventLevel.Information,
        ServerLogLevel.Error => LogEventLevel.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown server log level.")
    };
}
