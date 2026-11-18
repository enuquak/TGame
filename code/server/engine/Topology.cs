namespace TGame.Server.Engine;

public static class Topology
{
    public static IReadOnlyList<ProcessType> DialTargets(ProcessType type) => ServerConfiguration.DialTargets[type];

    public static bool Dials(ProcessType source, ProcessType target) => DialTargets(source).Contains(target);

    public static bool ArePeers(ProcessType first, ProcessType second) => Dials(first, second) || Dials(second, first);
}
