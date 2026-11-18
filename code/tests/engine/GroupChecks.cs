using TGame;

internal static class GroupChecks
{
    public static void Run()
    {
        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type, _ => 1);
        counts[ProcessType.Router] = 2;
        var group = new ServerGroup(counts);
        counts[ProcessType.Router] = 1;
        if (group.GetStartupMinimum(ProcessType.Router) != 2 || group.GetStartupMinimum(ProcessType.Base) != 1)
        {
            throw new Exception("Startup minimums must preserve the loaded snapshot independently of caller changes.");
        }

        string[] expected = ["Router1", "Router2"];
        var routers = group.Processes.Where(process => process.Type == ProcessType.Router);
        var single = group.Processes.Single(process => process.Type == ProcessType.Base);
        if (!routers.Select(process => process.Name).SequenceEqual(expected) ||
            single.Name != "Base1" || single.ChannelId != 1)
        {
            throw new Exception("Every instance must include its number, including a single instance and instance 1.");
        }

        Console.WriteLine("PASS explicit instance numbering starts at 1 for every process type.");
    }
}
