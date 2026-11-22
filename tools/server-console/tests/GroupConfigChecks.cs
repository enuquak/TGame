using System.Reflection;
using TGame;
using TGame.ServerConsole;

internal static class GroupConfigChecks
{
    public static void Run(string checkout)
    {
        var original = Environment.GetEnvironmentVariable(EngineEnvironment.Config);
        try
        {
            CheckSelectedConfiguration(checkout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EngineEnvironment.Config, original);
        }

        Console.WriteLine("PASS selected YAML, direct configuration paths, runtime directories and startup counts.");
    }

    private static void CheckSelectedConfiguration(string checkout)
    {
        var root = Path.Combine(checkout, "build", "group-config-check", Guid.NewGuid().ToString("N"));
        var configDirectory = Directory.CreateDirectory(Path.Combine(root, "code/server/config")).FullName;
        var consoleDirectory = Directory.CreateDirectory(Path.Combine(root, "tools/server-console")).FullName;
        File.WriteAllText(Path.Combine(consoleDirectory, "project.json"),
            """{"environmentName":"配置检查","commands":["Start"]}""");
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type.ToString(), _ => 1);
        var defaultConfiguration = Path.Combine(configDirectory, ServerConfiguration.FileName);
        WriteConfiguration(defaultConfiguration, counts);
        Environment.SetEnvironmentVariable(EngineEnvironment.Config, null);
        using (var defaults = new ConsoleSession(root))
        {
            if (defaults.Instances.Count != counts.Count)
            {
                throw new Exception("The console did not use the repository YAML entry point.");
            }
        }

        counts[nameof(ProcessType.Login)] = 2;
        counts[nameof(ProcessType.Base)] = 3;
        var selectedDirectory = Directory.CreateDirectory(Path.Combine(root, "selected")).FullName;
        var selected = Path.Combine(selectedDirectory, ServerConfiguration.FileName);
        WriteConfiguration(selected, counts);
        Environment.SetEnvironmentVariable(EngineEnvironment.Config, Path.GetRelativePath(root, selected));
        using var session = new ConsoleSession(root);
        if (session.Instances.Count != 11 ||
            session.Instances.Select(item => item.Name).Distinct().Count() != 11 ||
            session.Instances.Count(item => item.Role == nameof(ProcessType.Login)) != 2 ||
            session.Instances.Count(item => item.Role == nameof(ProcessType.Base)) != 3)
        {
            throw new Exception("The console did not expand the selected YAML instance counts.");
        }

        var configuration = new ServerConfiguration(selected);
        var group = configuration.Group;
        var sessionName = Guid.NewGuid().ToString("N");
        var sessions = Path.Combine(root, "logs/tools/server-console");
        var run = Directory.CreateDirectory(Path.Combine(sessions, sessionName)).FullName;
        File.WriteAllText(Path.Combine(run, ConsoleSession.SessionMarker), "");
        File.WriteAllText(Path.Combine(run, ServerRunFiles.DynamicDiscoveryMarker), "");
        File.WriteAllText(Path.Combine(sessions, "current"), sessionName);

        var build = Directory.CreateDirectory(Path.Combine(root, "build-output")).FullName;
        Directory.CreateDirectory(Path.Combine(build, "config"));
        File.WriteAllText(Path.Combine(build, "TGame.Server.exe"), "apphost");
        File.WriteAllText(Path.Combine(build, "config", ServerConfiguration.FileName), "stale: build output");
        var snapshot = Path.Combine(root, "build", "server-console", "runs", sessionName);
        var apphosts = group.Processes.Select(process => Path.Combine(snapshot, process.Name + "Server.exe")).ToArray();
        ConsoleSession.PrepareSnapshot(build, snapshot, apphosts);
        if (Directory.Exists(Path.Combine(snapshot, "config")) || File.Exists(Path.Combine(run, ServerConfiguration.FileName)))
        {
            throw new Exception("Executable copies must not save configuration snapshots.");
        }

        if (apphosts.Any(path => File.ReadAllText(path) != "apphost"))
        {
            throw new Exception("The snapshot did not create all planned process apphosts.");
        }

        using var restored = new ConsoleSession(root);
        if (restored.Instances.Count != 11)
        {
            throw new Exception("Reopening the console did not read the selected configuration.");
        }

        var startInfo = typeof(ConsoleSession).GetMethod("StartInfo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var start = (System.Diagnostics.ProcessStartInfo)startInfo.Invoke(restored, [restored.Instances[0], sessionName])!;
        if (start.ArgumentList.Count != 1 ||
            start.ArgumentList[0] != restored.Instances[0].Role ||
            start.Environment[EngineEnvironment.Config] != selected ||
            start.Environment[EngineEnvironment.RunDirectory] != run)
        {
            throw new Exception("A process must receive its type, the selected source configuration and its runtime directory.");
        }

        if (!File.ReadAllText(selected).Contains("include: [ops, process]") ||
            new ServerConfiguration(selected).RunDirectory != "logs/server-runs")
        {
            throw new Exception("Starting a run changed the source configuration.");
        }

        var validEntry = File.ReadAllText(defaultConfiguration).Replace("include: [ops, process]", "include: [ops]");
        validEntry += "\nconnections:\n" + string.Join("\n", Enum.GetValues<ProcessType>().Select(type => $"  {type}: []"));
        string[] invalid =
        [
            "[]", "{}", "processes: [bad", "processes: {Router: -1}", "processes: {Router: 1.5}",
            "processes: {Unknown: 1}", "processes: {Router: 1, Router: 2}",
            "processes:\n" + string.Join("\n", counts.Select(pair => $"  {pair.Key}: 0"))
        ];
        foreach (var yaml in invalid)
        {
            var contents = yaml;
            if (yaml.StartsWith("processes:", StringComparison.Ordinal))
            {
                contents = validEntry + "\n" + yaml;
            }
            File.WriteAllText(defaultConfiguration, contents);
            try
            {
                ServerGroup.Load(defaultConfiguration);
                throw new Exception("Accepted invalid counts, unknown types or duplicate configuration keys.");
            }
            catch (InvalidDataException)
            {
            }
        }

        File.Delete(Path.Combine(run, ServerRunFiles.DynamicDiscoveryMarker));
        try
        {
            using var unsupported = new ConsoleSession(root);
            throw new Exception("Accepted a run without the current identity format marker.");
        }
        catch (InvalidDataException)
        {
        }
    }

    private static void WriteConfiguration(string path, IReadOnlyDictionary<string, int> counts)
    {
        var directory = Path.GetDirectoryName(path)!;
        File.WriteAllText(path, """
            include: [ops, process]
            environment: snapshot-check
            loggingMinimumLevel: DEBUG
            runDirectory: logs/server-runs
            """);
        File.WriteAllText(Path.Combine(directory, "ops.yaml"), """
            mongo:
              endpoint: snapshot-host:27017
              databaseName: &database snapshot-check
              authenticationDatabase: *database
              userName: snapshot-user
              serverSelectionTimeoutSeconds: 5
            zooKeeperOptions:
              endpoint: 127.0.0.1:2181
              discoveryRoot: /snapshot-check
              workerRootPath: /snapshot-workers
              sessionTimeoutMilliseconds: 10000
              connectTimeoutMilliseconds: 10000
            """);
        var connections = Enum.GetValues<ProcessType>().Select(type =>
            $"  {type}: [{(type == ProcessType.Base ? nameof(ProcessType.Router) : "")}]");
        File.WriteAllText(Path.Combine(directory, "process.yaml"),
            "processes:\n" + string.Join("\n", counts.Select(pair => $"  {pair.Key}: {pair.Value}")) +
            "\nconnections:\n" + string.Join("\n", connections) + "\n");
    }
}
