using TGame;

internal static class GroupChecks
{
    public static void Run()
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
        counts[ProcessType.Router] = 2;
        var group = new ServerGroup(counts);
        string[] expected = ["Router1", "Router2"];
        var routers = group.Processes.Where(process => process.Type == ProcessType.Router);
        var baseName = ServerProcess.FormatName(nameof(ProcessType.Base), 1);
        if (!routers.Select(process => process.Name).SequenceEqual(expected) ||
            group.GetProcess(baseName).ChannelId != 1 ||
            group.Contains(nameof(ProcessType.Router)))
        {
            throw new Exception("Every instance must include its number, including a single instance and instance 1.");
        }

        Console.WriteLine("PASS explicit instance numbering starts at 1 for every process type.");
    }
}
