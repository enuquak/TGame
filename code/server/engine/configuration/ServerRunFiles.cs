using System;

namespace TGame;

public static class ServerRunFiles
{
    public const string GroupConfiguration = ServerConfiguration.FileName;
    public const string StopGroup = "stop";
    public const string StopRolePrefix = "stop-";
    public const string StopProcessPrefix = "stop-process-";
    public const string IdentityPrefix = "instance-";
    public const string DynamicDiscoveryMarker = "dynamic-discovery";

    public static string ProcessKey(int pid, long startedUtcTicks) =>
        FormattableString.Invariant($"{pid}-{startedUtcTicks}");
}
