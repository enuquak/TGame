using YamlDotNet.Serialization;
using TGame;
using TGame.Server.Engine;

internal static class ServerConfigurationChecks
{
    public static void Run(TestServices services)
    {
        var path = Path.GetTempFileName();
        var defaults = Path.Combine(AppContext.BaseDirectory, "config", ServerConfiguration.FileName);
        try
        {
            File.WriteAllText(path, "connections: {}");
            Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
            new YamlConfiguration(defaults).SaveSnapshot(path);
            var configuration = ServerConfiguration.Load(path);

            File.WriteAllText(path, "{}");
            Check(configuration.GetDialTargets(ProcessType.Base).SequenceEqual(new[] { ProcessType.Router, ProcessType.DBMgr }),
                "Base must retain Router and DBMgr type dependencies after its file changes.");
            Check(configuration.GetDialTargets(ProcessType.Router).Count == 0, "Router must not wait for incoming types.");
            var node = services.CreateNode(new ServerProcess(ProcessType.Router, 999), configuration);
            Check(node.FirstLinks.IsCompletedSuccessfully, "Valid identities must not be bounded by configured counts.");
            var independent = ServerConfiguration.Load(defaults);
            Check(!ReferenceEquals(configuration, independent), "Independent configurations share an instance.");

            var invalid = new YamlConfiguration(defaults).Read<Dictionary<string, string[]>>(ServerConfiguration.SectionName);
            var serializer = new SerializerBuilder().Build();
            invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Base)];
            File.WriteAllText(path, serializer.Serialize(new Dictionary<string, object> { [ServerConfiguration.SectionName] = invalid }));
            Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
            invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Router)];
            File.WriteAllText(path, serializer.Serialize(new Dictionary<string, object> { [ServerConfiguration.SectionName] = invalid }));
            Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
            invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Gate)];
            File.WriteAllText(path, serializer.Serialize(new Dictionary<string, object> { [ServerConfiguration.SectionName] = invalid }));
            Expect<InvalidDataException>(() => ServerConfiguration.Load(path));

            var cycle = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => Array.Empty<ProcessType>());
            cycle[ProcessType.Login] = [ProcessType.Base];
            cycle[ProcessType.Base] = [ProcessType.Scene];
            cycle[ProcessType.Scene] = [ProcessType.Login];
            Expect<InvalidDataException>(() =>
            {
                _ = new ServerConfiguration(cycle);
            });

            foreach (var name in new[] { "Router", "Router0", "Router01", "router1", "Unknown1", "1", "Router2147483648", "Router-1" })
            {
                Check(!ServerProcess.TryParse(name, out _), $"Invalid instance was accepted: {name}");
            }

            Check(ServerProcess.Parse("Router123").ChannelId == 123, "Number parsing failed.");
            Expect<InvalidDataException>(() => ServerProcess.ParseType("Router1"));
            Expect<InvalidDataException>(() => ServerProcess.ParseType("0"));
            var isolatedTargets = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => Array.Empty<ProcessType>());
            var isolated = new ServerConfiguration(isolatedTargets);
            var next = ServerConfiguration.Load(defaults);
            Check(!isolated.Dials(ProcessType.Scene, ProcessType.Router) && next.Dials(ProcessType.Scene, ProcessType.Router),
                "Loading another configuration changed an existing snapshot.");
        }
        finally
        {
            File.Delete(path);
        }

        Console.WriteLine("PASS type topology validation, canonical identities, immutable configuration and independent snapshots.");
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
