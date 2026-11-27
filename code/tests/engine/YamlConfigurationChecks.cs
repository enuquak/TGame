using YamlDotNet.Serialization;
using TGame;
using TGame.Server;

internal static class YamlConfigurationChecks
{
    public static void Run()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "config", "server.yaml");
        var settings = ServerConfiguration.Load(source);
        if (settings.LoggingMinimumLevel != ServerLogLevel.Debug ||
            settings.Mongo!.DatabaseName != "tgame_test" ||
            settings.Mongo.UserName != settings.Redis!.UserName ||
            settings.Mongo.AuthenticationDatabase != settings.Mongo.DatabaseName)
        {
            throw new Exception("The unified entry did not load nested settings and YAML aliases.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"tgame-yaml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var entry = Path.Combine(directory, ServerConfiguration.FileName);
            var first = Path.Combine(directory, "first.yaml");
            var second = Path.Combine(directory, "second.yaml");
            const string mongo = "mongo:\n  endpoint: &shared alpha\n  databaseName: *shared\n";
            const string redis = "redis:\n  endpoint: &shared beta\n  userName: *shared\n";
            File.WriteAllText(first, mongo);
            File.WriteAllText(second, redis);
            var baseline = ConfigurationFixture.CreateDocument();
            baseline[ServerConfiguration.RunDirectoryKey] = "logs/original";
            var core = new SerializerBuilder().Build().Serialize(baseline);
            void WriteEntry(string extra) => File.WriteAllText(entry, core + "\n" + extra + "\n");

            WriteEntry("include: [first, second]");
            var configuration = new ServerConfiguration(entry);
            Check(configuration.Mongo!.DatabaseName == "alpha", "First document alias changed.");
            Check(configuration.Redis!.UserName == "beta", "Anchor names leaked across files.");
            Check(configuration.ResolveRunDirectory(directory) == Path.Combine(directory, "logs", "original"), "Relative runtime path changed.");

            File.Delete(first);
            File.Delete(second);
            Check(configuration.Redis!.UserName == "beta", "Loaded settings still depend on source include files.");
            Expect<FileNotFoundException>(() => new ServerConfiguration(entry));

            File.WriteAllText(first, mongo);
            File.WriteAllText(second, redis);
            _ = new ServerConfiguration(entry);
            File.WriteAllText(second, "mongo:\n  endpoint: second\n");
            ExpectInvalid(() => new ServerConfiguration(entry), "Duplicate top-level configuration key 'mongo'");
            WriteEntry("include: [first]\nmongo: {endpoint: entry}");
            ExpectInvalid(() => new ServerConfiguration(entry), "Duplicate top-level configuration key 'mongo'");

            // 空 include 是合法输入，重复文件名只能由 include 规则拒绝，不能误命中字段重名。
            File.WriteAllText(first, "{}");
            WriteEntry("include: [first]");
            _ = new ServerConfiguration(entry);
            foreach (var invalid in new[] { "include: first", "include: [../first]", "include: [first.yaml]", "include: [first, first]" })
            {
                WriteEntry(invalid);
                ExpectInvalid(() => new ServerConfiguration(entry), "include");
            }

            WriteEntry("environment: duplicate");
            ExpectInvalid(() => new ServerConfiguration(entry), "Invalid YAML configuration");
            WriteEntry("mongo:\n  endpoint: first\n");
            _ = new ServerConfiguration(entry);
            WriteEntry("mongo:\n  endpoint: first\n  endpoint: second\n");
            ExpectInvalid(() => new ServerConfiguration(entry), "Invalid YAML configuration");
            foreach (var invalid in new[] { "[one, two]", core + "\n---\n" + core })
            {
                File.WriteAllText(entry, invalid);
                ExpectInvalid(() => new ServerConfiguration(entry), "one YAML mapping document");
            }

            WriteEntry("include: [first]");
            File.WriteAllText(first, "include: [server]\n");
            ExpectInvalid(() => new ServerConfiguration(entry), "Circular configuration include");
            WriteEntry("include: [first, second]");
            File.WriteAllText(first, "mongo:\n  endpoint: &onlyHere value\n");
            File.WriteAllText(second, "redis:\n  endpoint: value\n");
            _ = new ServerConfiguration(entry);
            File.WriteAllText(second, "redis:\n  endpoint: *onlyHere\n");
            ExpectInvalid(() => new ServerConfiguration(entry), "Invalid YAML configuration");
            WriteEntry("mongo: &recursive {endpoint: *recursive}");
            ExpectInvalid(() => new ServerConfiguration(entry), "aliases must be acyclic");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("PASS unified YAML, independent anchors, strict includes/duplicates, invalid documents and immutable loaded settings.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static void ExpectInvalid(Action action, string reason)
    {
        try
        {
            action();
        }
        catch (InvalidDataException error)
        {
            Check(error.Message.Contains(reason, StringComparison.OrdinalIgnoreCase),
                $"Expected rejection for '{reason}', got: {error.Message}");
            return;
        }

        throw new Exception($"Expected invalid configuration for '{reason}'.");
    }
}
