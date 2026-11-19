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
            CheckSnapshot(checkout);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EngineEnvironment.Config, original);
        }

        Console.WriteLine("PASS selected YAML, expanded snapshots, startup counts and restored group isolation.");
    }

    private static void CheckSnapshot(string checkout)
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
        ConsoleSession.PrepareSnapshot(build, snapshot, configuration, run, root, apphosts);
        var snapshotFile = Path.Combine(snapshot, "config", ServerConfiguration.FileName);
        var savedFile = Path.Combine(run, ServerConfiguration.FileName);
        File.Copy(snapshotFile, savedFile);

        // 源文件变更或删除后，本轮两个快照仍须独立包含同一份完整配置。
        counts[nameof(ProcessType.Login)] = 1;
        counts[nameof(ProcessType.Base)] = 1;
        WriteConfiguration(defaultConfiguration, counts);
        File.Delete(selected);
        File.Delete(Path.Combine(selectedDirectory, "ops.yaml"));
        File.Delete(Path.Combine(selectedDirectory, "process.yaml"));
        foreach (var file in new[] { snapshotFile, savedFile })
        {
            var saved = new ServerConfiguration(file);
            var startup = saved.Group;
            var connections = saved.Read<Dictionary<string, string[]>>("connections");
            if (startup.GetStartupMinimum(ProcessType.Login) != 2 ||
                startup.GetStartupMinimum(ProcessType.Base) != 3 ||
                saved.Environment != "snapshot-check" ||
                saved.Mongo?.Endpoint != "snapshot-host:27017" ||
                !connections[nameof(ProcessType.Base)].SequenceEqual([nameof(ProcessType.Router)]) ||
                saved.ResolveRunDirectory(root) != run ||
                File.ReadAllText(file).Contains("include:"))
            {
                throw new Exception("The saved YAML lost counts, included settings, connections or its run directory.");
            }
        }

        if (apphosts.Any(path => File.ReadAllText(path) != "apphost"))
        {
            throw new Exception("The snapshot did not create all planned process apphosts.");
        }

        Environment.SetEnvironmentVariable(EngineEnvironment.Config, Path.Combine(root, "missing-environment.yaml"));
        using var restored = new ConsoleSession(root);
        if (restored.Instances.Count != 11)
        {
            throw new Exception("Restoring a running group used current source counts instead of its snapshot.");
        }

        var startInfo = typeof(ConsoleSession).GetMethod("StartInfo", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var start = (System.Diagnostics.ProcessStartInfo)startInfo.Invoke(restored, [restored.Instances[0], sessionName])!;
        if (start.ArgumentList.Count != 1 ||
            start.ArgumentList[0] != restored.Instances[0].Role ||
            start.Environment[EngineEnvironment.Config] != savedFile)
        {
            throw new Exception("Restoring a process must pass only its type and the saved configuration.");
        }

        File.Delete(snapshotFile);
        try
        {
            startInfo.Invoke(restored, [restored.Instances[0], sessionName]);
            throw new Exception("An incomplete binary snapshot must require build and restart.");
        }
        catch (TargetInvocationException error) when (
            error.InnerException is InvalidOperationException inner && inner.Message.Contains("编译并重启"))
        {
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
            zooKeeper:
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
