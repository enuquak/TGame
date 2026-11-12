namespace TGame.Server;

internal static class Topology
{
    // One local instance per role. Each pair opens one stream.
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

    public static int Degree(string role) => Dials[role].Length + Dials.Values.Count(v => v.Contains(role));
}
