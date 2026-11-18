using TGame;
using TGame.Server.Engine;

internal static class ServerConfigurationChecks
{
    public static void Run()
    {
        var router = ServerProcess.FormatName(nameof(ProcessType.Router), 1);
        var path = Path.GetTempFileName();
        try
        {
            Expect<InvalidOperationException>(() => ServerConfiguration.GetProcess(router));
            File.WriteAllText(path, "{");
            Expect<InvalidDataException>(() => ServerConfiguration.Initialize(path));
            Expect<InvalidOperationException>(() => _ = ServerConfiguration.Processes);

            var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 0);
            counts[ProcessType.Router] = 2;
            new ServerGroup(counts).Save(path);
            using (var lifetime = ServerConfiguration.Initialize(path))
            {
                File.WriteAllText(path, "{");
                if (ServerConfiguration.Processes.Count != 2 ||
                    ServerConfiguration.GetProcessType(router) != ProcessType.Router ||
                    ServerConfiguration.GetProcess(router).ChannelId != 1 ||
                    ServerConfiguration.Contains(nameof(ProcessType.Router)))
                {
                    throw new Exception("Configuration must retain its validated startup snapshot and instance identities.");
                }

                Expect<InvalidDataException>(() => ServerConfiguration.GetProcess(
                    ServerProcess.FormatName(nameof(ProcessType.Router), 3)));
                Expect<InvalidOperationException>(() => ServerConfiguration.Initialize(path));

                using var node = new ServerNode(router);
                if (!node.FirstLinks.IsCompletedSuccessfully || node.Role != nameof(ProcessType.Router))
                {
                    throw new Exception("Nodes must obtain their role and configured peers from the shared configuration.");
                }
            }

            Expect<InvalidOperationException>(() => ServerConfiguration.GetProcess(router));
            counts[ProcessType.Router] = 1;
            new ServerGroup(counts).Save(path);
            var previous = ServerConfiguration.Initialize(path);
            previous.Dispose();
            using var next = ServerConfiguration.Initialize(path);
            previous.Dispose();
            if (ServerConfiguration.Processes.Count != 1)
            {
                throw new Exception("Disposing an old configuration must not clear the next lifecycle.");
            }
        }
        finally
        {
            File.Delete(path);
        }

        Console.WriteLine("PASS configuration loading, snapshot, instance lookup and singleton lifecycle.");
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
