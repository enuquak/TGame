namespace TGame.Server.Engine;

public static class Topology
{
    // The dial direction avoids opening two streams for the same pair of instances.
    public static readonly Dictionary<string, string[]> Dials = new()
    {
        [nameof(ProcessType.Gate)] = [nameof(ProcessType.Login), nameof(ProcessType.Base)],
        [nameof(ProcessType.Login)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.Base)] = [nameof(ProcessType.Router), nameof(ProcessType.DBMgr)],
        [nameof(ProcessType.Scene)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.DBMgr)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.Profile)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.GM)] = [nameof(ProcessType.Router)],
        [nameof(ProcessType.Router)] = []
    };

    public static string[] DialTargets(ServerGroup group, string name)
    {
        var role = group.GetProcess(name).Role;
        var targets = new List<string>();
        foreach (var process in group.Processes)
        {
            if (Dials[role].Contains(process.Role))
            {
                targets.Add(process.Name);
            }
        }

        return targets.ToArray();
    }

    public static HashSet<string> Peers(ServerGroup group, string name)
    {
        var role = group.GetProcess(name).Role;
        var peers = new HashSet<string>(DialTargets(group, name), StringComparer.Ordinal);
        foreach (var process in group.Processes)
        {
            if (Dials[process.Role].Contains(role))
            {
                peers.Add(process.Name);
            }
        }

        return peers;
    }
}
