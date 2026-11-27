using YamlDotNet.Serialization;
using TGame;
using TGame.Server.Engine;

internal static class ServerConfigurationChecks
{
    public static void Run(TestServices services)
    {
        var path = Path.GetTempFileName();
        var defaults = Path.Combine(AppContext.BaseDirectory, "config", ServerConfiguration.FileName);
        var baseline = ServerConfiguration.Load(defaults);
        var document = ConfigurationFixture.CreateDocument();
        var baselineTargets = Enum.GetValues<ProcessType>().ToDictionary(type => type.ToString(),
            type => baseline.GetDialTargets(type).Select(target => target.ToString()).ToArray());
        void WriteConnections(IReadOnlyDictionary<string, string[]> configured)
        {
            document[ServerConfiguration.SectionName] = configured;
            File.WriteAllText(path, new SerializerBuilder().Build().Serialize(document));
        }

        try
        {
            WriteConnections(new Dictionary<string, string[]>());
            Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
            WriteConnections(baselineTargets);
            var configuration = ServerConfiguration.Load(path);

            File.WriteAllText(path, "{}");
            Check(configuration.GetDialTargets(ProcessType.Base).SequenceEqual(new[] { ProcessType.Router, ProcessType.DBMgr }),
                "Base must retain Router and DBMgr type dependencies after its file changes.");
            Check(configuration.GetDialTargets(ProcessType.Router).Count == 0, "Router must not wait for incoming types.");
            var node = services.CreateNode(new ServerProcess(ProcessType.Router, 999), configuration);
            Check(node.FirstLinks.IsCompletedSuccessfully, "Valid identities must not be bounded by configured counts.");
            var independent = ServerConfiguration.Load(defaults);
            Check(!ReferenceEquals(configuration, independent), "Independent configurations share an instance.");

            var invalid = new Dictionary<string, string[]>(baselineTargets);
            invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Base)];
            WriteConnections(invalid);
            Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
            invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Router)];
            WriteConnections(invalid);
            Expect<InvalidDataException>(() => ServerConfiguration.Load(path));
            invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Gate)];
            WriteConnections(invalid);
            Expect<InvalidDataException>(() => ServerConfiguration.Load(path));

            var cycle = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => Array.Empty<ProcessType>());
            cycle[ProcessType.Login] = [ProcessType.Base];
            cycle[ProcessType.Base] = [ProcessType.Scene];
            cycle[ProcessType.Scene] = [ProcessType.Login];
            WriteConnections(cycle.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.Select(type => type.ToString()).ToArray()));
            Expect<InvalidDataException>(() => ServerConfiguration.Load(path));

            foreach (var name in new[] { "Router", "Router0", "Router01", "router1", "Unknown1", "1", "Router2147483648", "Router-1" })
            {
                Check(!ServerProcess.TryParse(name, out _), $"Invalid instance was accepted: {name}");
            }

            Check(ServerProcess.Parse("Router123").ChannelId == 123, "Number parsing failed.");
            Expect<InvalidDataException>(() => ServerProcess.ParseType("Router1"));
            Expect<InvalidDataException>(() => ServerProcess.ParseType("0"));
            var isolatedTargets = Enum.GetValues<ProcessType>().ToDictionary(type => type.ToString(), _ => Array.Empty<string>());
            WriteConnections(isolatedTargets);
            var isolated = ServerConfiguration.Load(path);
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
