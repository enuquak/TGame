using System;
using System.IO;

namespace TGame;

public static class ServerRunFiles
{
    public const string StopGroup = "stop";
    public const string StopRolePrefix = "stop-";
    public const string StopProcessPrefix = "stop-process-";
    public const string IdentityPrefix = "instance-";
    public const string DynamicDiscoveryMarker = "dynamic-discovery";

    public static string ProcessKey(int pid, long startedUtcTicks) =>
        FormattableString.Invariant($"{pid}-{startedUtcTicks}");

    public static string StartupLogDirectory(string runDirectory, int pid, long startedUtcTicks) =>
        Path.Combine(runDirectory, "startup", ProcessKey(pid, startedUtcTicks));
}
