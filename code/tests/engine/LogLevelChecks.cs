using System.Text.Json;
using System.Globalization;
using TGame;
using TGame.Server;
using TGame.Server.Engine;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

internal static class LogLevelChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tgame-log-levels-{Guid.NewGuid():N}");
        var process = new ServerProcess(ProcessType.Login, 1);
        var originalRoot = Environment.GetEnvironmentVariable(EngineEnvironment.LogRoot);
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        Directory.CreateDirectory(root);
        try
        {
            CheckSettings(root);
            foreach (var minimum in Enum.GetValues<ServerLogLevel>())
            {
                using var output = new StringWriter();
                Console.SetOut(output);
                using var log = new GameLog();
                log.SetMinimumLevel(minimum);
                WriteSamples(log, process);
                CheckRows(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), minimum, process.Role);
            }

            {
                using var output = new StringWriter();
                using var error = new StringWriter();
                Console.SetOut(output);
                Console.SetError(error);
                using var log = new GameLog();
                log.Error(ServerLogEvents.ProcessFailed, process.Role, detail: "进程失败", bothPrintConsole: true);
                CheckRows(error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), ServerLogLevel.Error, process.Role);
                if (output.ToString().Length != 0)
                {
                    throw new Exception("Console mirroring duplicated an error on standard output before opening a log file.");
                }
            }

            foreach (var minimum in Enum.GetValues<ServerLogLevel>())
            {
                var logRoot = Path.Combine(root, minimum.ToString());
                using var output = new StringWriter();
                using var error = new StringWriter();
                Console.SetOut(output);
                Console.SetError(error);
                Environment.SetEnvironmentVariable(EngineEnvironment.LogRoot, logRoot);
                using (var log = new GameLog())
                {
                    log.SetMinimumLevel(minimum);
                    log.OpenServer(process);
                    ExpectInvalidOperation(() => log.OpenServer(process), "Opening a second log file must fail.");
                    WriteSamples(log, process);
                    if (output.ToString().Length != 0 || error.ToString().Length != 0)
                    {
                        throw new Exception("File logging printed to the console without an explicit request.");
                    }

                    log.Error(ServerLogEvents.ProcessFailed, process.Role,
                        detail: "同时写入日志与终端：'错误'\n详细信息", bothPrintConsole: true);
                }

                var files = Directory.GetFiles(logRoot, "*.log", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    if (Path.GetDirectoryName(file) != Path.Combine(logRoot, "Login1") ||
                        !DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMMddHHmm",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out var hour) || hour.Minute != 0)
                    {
                        throw new Exception("Server logs must use Login1/yyyyMMddHH00.log for hourly files.");
                    }
                }

                var lines = files.Order(StringComparer.Ordinal).SelectMany(File.ReadLines).ToArray();
                CheckRows(lines[..^1], minimum, process.Name);
                var consoleLines = error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                CheckRows(consoleLines, ServerLogLevel.Error, process.Name);
                if (output.ToString().Length != 0 || consoleLines[0] != lines[^1])
                {
                    throw new Exception("Console mirroring must write the same error record exactly once to standard error.");
                }
            }

            CheckLifecycle(process);
            ServerLogFileChecks.Run(Path.Combine(root, "rolling"));
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable(EngineEnvironment.LogRoot, originalRoot);
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("PASS logger lifecycle, levels, JSON, console mirroring, hourly/size filenames, restart append and concurrent writes.");
    }

    private static void CheckLifecycle(ServerProcess process)
    {
        using var output = new StringWriter();
        var originalOutput = Console.Out;
        Console.SetOut(output);
        try
        {
            var previous = new GameLog();
            using var current = new GameLog();
            previous.SetMinimumLevel(ServerLogLevel.Debug);
            current.SetMinimumLevel(ServerLogLevel.Info);
            previous.Dispose();
            ExpectDisposed(() => previous.Info(ServerLogEvents.Listen, process.Role));
            ExpectDisposed(() => previous.SetMinimumLevel(ServerLogLevel.Info));
            ExpectDisposed(() => previous.OpenServer(process));
            previous.Dispose();
            WriteSamples(current, process);
            CheckRows(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
                ServerLogLevel.Info, process.Role);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    private static void ExpectDisposed(Action action)
    {
        try
        {
            action();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new Exception("Using a disposed logger must fail.");
    }

    private static void ExpectInvalidOperation(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new Exception(message);
    }

    private static void CheckSettings(string root)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "config", ServerConfiguration.FileName);
        if (ServerConfiguration.Load(source).LoggingMinimumLevel != ServerLogLevel.Debug)
        {
            throw new Exception("The test environment must default to DEBUG.");
        }

        var path = Path.Combine(root, "server.yaml");
        var values = new ServerConfiguration(source).Read<Dictionary<string, object>>();
        var yaml = new SerializerBuilder().Build().Serialize(values).Replace("loggingMinimumLevel: DEBUG", "");
        File.WriteAllText(path, yaml);
        if (ServerConfiguration.Load(path).LoggingMinimumLevel != ServerLogLevel.Info)
        {
            throw new Exception("An unspecified minimum log level must default to INFO.");
        }

        foreach (var minimum in Enum.GetValues<ServerLogLevel>())
        {
            File.WriteAllText(path, yaml.Replace("...", "") + $"\nloggingMinimumLevel: {minimum.ToString().ToUpperInvariant()}\n");
            if (ServerConfiguration.Load(path).LoggingMinimumLevel != minimum)
            {
                throw new Exception($"Minimum log level {minimum} was not loaded.");
            }
        }

        foreach (var invalid in new[] { "TRACE", "WARNING", "999" })
        {
            File.WriteAllText(path, yaml.Replace("...", "") + $"\nloggingMinimumLevel: {invalid}\n");
            try
            {
                ServerConfiguration.Load(path);
                throw new Exception($"Invalid log level {invalid} was accepted.");
            }
            catch (InvalidDataException)
            {
            }
            catch (YamlException)
            {
            }
        }
    }

    private static void WriteSamples(GameLog log, ServerProcess process)
    {
        log.Debug(ServerLogEvents.ZkDiscover, process.Role, detail: "发现细节");
        log.Info(ServerLogEvents.Listen, process.Role, detail: "开始监听");
        log.Error(ServerLogEvents.ProcessFailed, process.Role, detail: "进程失败");
    }

    private static void CheckRows(string[] lines, ServerLogLevel minimum, string instance)
    {
        string[] expected = minimum switch
        {
            ServerLogLevel.Debug => ["DEBUG", "INFO", "ERROR"],
            ServerLogLevel.Info => ["INFO", "ERROR"],
            ServerLogLevel.Error => ["ERROR"],
            _ => throw new ArgumentOutOfRangeException(nameof(minimum))
        };
        if (lines.Length != expected.Length)
        {
            throw new Exception($"Minimum level {minimum} wrote {lines.Length} rows, expected {expected.Length}.");
        }

        for (int index = 0; index < lines.Length; index++)
        {
            using var row = JsonDocument.Parse(lines[index]);
            var value = row.RootElement;
            if (value.GetProperty(ServerLogFields.Level).GetString() != expected[index] ||
                value.GetProperty(ServerLogFields.Instance).GetString() != instance ||
                value.GetProperty(ServerLogFields.Role).GetString() != nameof(ProcessType.Login) ||
                value.GetProperty(ServerLogFields.Pid).GetInt32() != Environment.ProcessId ||
                string.IsNullOrEmpty(value.GetProperty(ServerLogFields.Event).GetString()) ||
                string.IsNullOrEmpty(value.GetProperty(ServerLogFields.Detail).GetString()))
            {
                throw new Exception($"Unexpected log fields at minimum level {minimum}: {lines[index]}");
            }
        }
    }
}
