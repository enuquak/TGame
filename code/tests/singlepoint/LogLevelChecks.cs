using System.Text.Json;
using TGame;
using TGame.Server;
using TGame.Server.Engine;
using YamlDotNet.Core;

internal static class LogLevelChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tgame-log-levels-{Guid.NewGuid():N}");
        var process = new ServerProcess(ProcessType.Login, 1);
        var originalRoot = Environment.GetEnvironmentVariable(EngineEnvironment.LogRoot);
        var originalOutput = Console.Out;
        Directory.CreateDirectory(root);
        try
        {
            CheckSettings(root);
            foreach (var minimum in Enum.GetValues<ServerLogLevel>())
            {
                using var output = new StringWriter();
                Console.SetOut(output);
                GameLog.SetMinimumLevel(minimum);
                WriteSamples(process);
                CheckRows(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), minimum, process.Role);
            }

            Console.SetOut(originalOutput);
            foreach (var minimum in Enum.GetValues<ServerLogLevel>())
            {
                var logRoot = Path.Combine(root, minimum.ToString());
                Environment.SetEnvironmentVariable(EngineEnvironment.LogRoot, logRoot);
                GameLog.SetMinimumLevel(minimum);
                using (GameLog.OpenServer(process))
                {
                    WriteSamples(process);
                }

                var files = Directory.GetFiles(logRoot, "*.log", SearchOption.AllDirectories);
                var lines = files.Order(StringComparer.Ordinal).SelectMany(File.ReadLines).ToArray();
                CheckRows(lines, minimum, process.Name);
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            GameLog.SetMinimumLevel(ServerLogLevel.Info);
            Environment.SetEnvironmentVariable(EngineEnvironment.LogRoot, originalRoot);
            Directory.Delete(root, recursive: true);
        }

        Console.WriteLine("PASS DEBUG/INFO/ERROR filtering, JSON fields and server log configuration.");
    }

    private static void CheckSettings(string root)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "config", "server.local.yaml");
        if (ServerSettings.Load(source).MinimumLogLevel != ServerLogLevel.Debug)
        {
            throw new Exception("The test environment must default to DEBUG.");
        }

        var yaml = File.ReadAllText(source).Replace("minimumLogLevel: DEBUG", "");
        var path = Path.Combine(root, "server.yaml");
        File.WriteAllText(path, yaml);
        if (ServerSettings.Load(path).MinimumLogLevel != ServerLogLevel.Info)
        {
            throw new Exception("An unspecified minimum log level must default to INFO.");
        }

        foreach (var minimum in Enum.GetValues<ServerLogLevel>())
        {
            File.WriteAllText(path, yaml + $"\nminimumLogLevel: {minimum.ToString().ToUpperInvariant()}\n");
            if (ServerSettings.Load(path).MinimumLogLevel != minimum)
            {
                throw new Exception($"Minimum log level {minimum} was not loaded.");
            }
        }

        foreach (var invalid in new[] { "TRACE", "WARNING", "999" })
        {
            File.WriteAllText(path, yaml + $"\nminimumLogLevel: {invalid}\n");
            try
            {
                ServerSettings.Load(path);
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

    private static void WriteSamples(ServerProcess process)
    {
        GameLog.Debug(ServerLogEvents.EntityRoute, process.Role, detail: "路由细节");
        GameLog.Info(ServerLogEvents.Listen, process.Role, detail: "开始监听");
        GameLog.Error(ServerLogEvents.ProcessFailed, process.Role, detail: "进程失败");
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
