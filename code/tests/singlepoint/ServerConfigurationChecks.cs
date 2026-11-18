using System.Text.Json;
using TGame;
using TGame.Server.Engine;

internal static class ServerConfigurationChecks
{
    public static void Run()
    {
        var path = Path.GetTempFileName();
        var defaults = Path.Combine(AppContext.BaseDirectory, "config", ServerConfiguration.FileName);
        try
        {
            Expect<InvalidOperationException>(() => _ = ServerConfiguration.DialTargets);
            File.WriteAllText(path, "{}");
            Expect<InvalidDataException>(() => ServerConfiguration.Initialize(path));
            File.Copy(defaults, path, true);
            using (ServerConfiguration.Initialize(path))
            {
                File.WriteAllText(path, "{}");
                Check(Topology.DialTargets(ProcessType.Base).SequenceEqual(new[] { ProcessType.Router, ProcessType.DBMgr }),
                    "Base must retain Router and DBMgr type dependencies after its file changes.");
                Check(Topology.DialTargets(ProcessType.Router).Count == 0, "Router must not wait for incoming types.");
                using var node = new ServerNode("Router999");
                Check(node.FirstLinks.IsCompletedSuccessfully, "Valid identities must not be bounded by configured counts.");
                Expect<InvalidOperationException>(() => ServerConfiguration.Initialize(defaults));
            }

            var invalid = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(defaults))!;
            invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Base)];
            File.WriteAllText(path, JsonSerializer.Serialize(invalid));
            Expect<InvalidDataException>(() => ServerConfiguration.Initialize(path));
            invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Router)];
            File.WriteAllText(path, JsonSerializer.Serialize(invalid));
            Expect<InvalidDataException>(() => ServerConfiguration.Initialize(path));
            invalid[nameof(ProcessType.Router)] = [nameof(ProcessType.Gate)];
            File.WriteAllText(path, JsonSerializer.Serialize(invalid));
            Expect<InvalidDataException>(() => ServerConfiguration.Initialize(path));

            var cycle = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => Array.Empty<ProcessType>());
            cycle[ProcessType.Login] = [ProcessType.Base];
            cycle[ProcessType.Base] = [ProcessType.Scene];
            cycle[ProcessType.Scene] = [ProcessType.Login];
            Expect<InvalidDataException>(() =>
            {
                using var rejected = ServerConfiguration.Initialize(cycle);
            });

            foreach (var name in new[] { "Router", "Router0", "Router01", "router1", "Unknown1", "1", "Router2147483648", "Router-1" })
            {
                Check(!ServerProcess.TryParse(name, out _), $"Invalid instance was accepted: {name}");
            }

            Check(ServerProcess.Parse("Router123").ChannelId == 123, "Number parsing failed.");
            Expect<InvalidDataException>(() => ServerProcess.ParseType("Router1"));
            Expect<InvalidDataException>(() => ServerProcess.ParseType("0"));
            var previous = ServerConfiguration.Initialize(defaults);
            previous.Dispose();
            using var next = ServerConfiguration.Initialize(defaults);
            previous.Dispose();
            Check(Topology.Dials(ProcessType.Scene, ProcessType.Router), "Old disposal cleared the current configuration.");
        }
        finally
        {
            File.Delete(path);
        }

        Console.WriteLine("PASS type topology validation, canonical identities, immutable configuration and singleton lifecycle.");
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
