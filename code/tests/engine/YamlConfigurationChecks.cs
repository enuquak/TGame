using TGame;
using TGame.Server;

internal static class YamlConfigurationChecks
{
    public static void Run()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "config", "server.yaml");
        var settings = ServerSettings.Load(source);
        if (settings.MinimumLogLevel != ServerLogLevel.Debug || settings.MongoDatabase != "tgame_test" ||
            settings.MongoUser != settings.RedisUser || settings.Mongo!.AuthenticationDatabase != settings.MongoDatabase)
        {
            throw new Exception("The unified entry did not load nested settings and YAML aliases.");
        }

        var directory = Path.Combine(Path.GetTempPath(), $"tgame-yaml-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var entry = Path.Combine(directory, YamlConfiguration.FileName);
            var first = Path.Combine(directory, "first.yaml");
            var second = Path.Combine(directory, "second.yaml");
            File.WriteAllText(first, "first:\n  value: &shared alpha\n  alias: *shared\n");
            File.WriteAllText(second, "second:\n  value: &shared beta\n  alias: *shared\n");
            File.WriteAllText(entry, "include: [first, second]\nrunDirectory: logs/original\n");
            var configuration = new YamlConfiguration(entry);
            Check(configuration.Read<Dictionary<string, string>>("first")["alias"] == "alpha", "First document alias changed.");
            Check(configuration.Read<Dictionary<string, string>>("second")["alias"] == "beta", "Anchor names leaked across files.");
            Check(configuration.ResolveRunDirectory(directory) == Path.Combine(directory, "logs", "original"), "Relative runtime path changed.");

            var snapshot = Path.Combine(directory, "snapshot.yaml");
            configuration.SaveSnapshot(snapshot, "logs/current");
            File.Delete(first);
            File.Delete(second);
            var saved = new YamlConfiguration(snapshot);
            Check(saved.Read<Dictionary<string, string>>("second")["alias"] == "beta", "Snapshot still depends on source include files.");
            Check(saved.Read<string>(YamlConfiguration.RunDirectoryKey) == "logs/current", "Snapshot runtime directory was not updated.");
            Check(configuration.Read<string>(YamlConfiguration.RunDirectoryKey) == "logs/original", "Saving a snapshot mutated its source instance.");
            Expect<FileNotFoundException>(() => new YamlConfiguration(entry));

            File.WriteAllText(first, "shared: first\n");
            File.WriteAllText(second, "shared: second\n");
            Expect<InvalidDataException>(() => new YamlConfiguration(entry));
            File.WriteAllText(entry, "include: [first]\nshared: entry\n");
            Expect<InvalidDataException>(() => new YamlConfiguration(entry));

            foreach (var invalid in new[] { "include: first", "include: [../first]", "include: [first.yaml]", "include: [first, first]",
                "value: 1\nvalue: 2", "values:\n  value: 1\n  value: 2", "[one, two]", "---\na: 1\n---\nb: 2" })
            {
                File.WriteAllText(entry, invalid);
                Expect<InvalidDataException>(() => new YamlConfiguration(entry));
            }

            File.WriteAllText(entry, "include: [first]\n");
            File.WriteAllText(first, "include: [server]\n");
            Expect<InvalidDataException>(() => new YamlConfiguration(entry));
            File.WriteAllText(entry, "include: [first, second]\n");
            File.WriteAllText(first, "first: &onlyHere value\n");
            File.WriteAllText(second, "second: *onlyHere\n");
            Expect<InvalidDataException>(() => new YamlConfiguration(entry));
            File.WriteAllText(entry, "value: &recursive [*recursive]\n");
            Expect<InvalidDataException>(() => new YamlConfiguration(entry));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("PASS unified YAML, independent anchors, strict includes/duplicates, invalid documents and standalone immutable snapshots.");
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
}
