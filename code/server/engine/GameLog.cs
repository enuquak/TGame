using TGame.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TGame.Server.Engine;

public sealed class GameLog : IDisposable
{
    private const long FileSizeLimit = 10 * 1024 * 1024;
    private bool disposed;
    private readonly object changes = new();
    private string? processName;
    private Logger? serverLog;
    private readonly LoggingLevelSwitch minimumLevel = new();

    public void OpenServer(ServerProcess server)
    {
        lock (changes)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            OpenServerCore(server);
        }
    }

    private void OpenServerCore(ServerProcess server)
    {
        if (serverLog is not null)
        {
            throw new InvalidOperationException("The server log file is already open.");
        }

        var root = Environment.GetEnvironmentVariable(EngineEnvironment.LogRoot) ?? Path.Combine("logs", "server");
        var directory = Directory.CreateDirectory(Path.Combine(root, server.Name));
        var logger = new LoggerConfiguration().MinimumLevel.ControlledBy(minimumLevel)
            .WriteTo.Sink(new ServerFileSink(directory.FullName, FileSizeLimit)).CreateLogger();
        processName = server.Name;
        Volatile.Write(ref serverLog, logger);
    }

    public void Dispose()
    {
        lock (changes)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            serverLog?.Dispose();
        }
    }

    public void SetMinimumLevel(ServerLogLevel level)
    {
        lock (changes)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            minimumLevel.MinimumLevel = ToSerilogLevel(level);
        }
    }

    public void Debug(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null) =>
        Write(ServerLogLevel.Debug, ev, role, peer, id, entity, link, detail);

    public void Info(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null) =>
        Write(ServerLogLevel.Info, ev, role, peer, id, entity, link, detail);

    public void Error(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null, bool bothPrintConsole = false) =>
        Write(ServerLogLevel.Error, ev, role, peer, id, entity, link, detail, bothPrintConsole);

    private void Write(ServerLogLevel level, string ev, string role, string? peer, string? id,
        string? entity, string? link, string? detail, bool bothPrintConsole = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
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
