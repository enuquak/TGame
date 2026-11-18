namespace TGame;

public static class ServerLogEvents
{
    public const string LegacyReady = "READY";
    public const string InitStepCompleted = "INIT_STEP_COMPLETED";
    public const string Listen = "LISTEN";
    public const string ProcessFailed = "PROCESS_FAILED";
    public const string ProcessReady = "PROCESS_READY";
    public const string ZkRegister = "ZK_REGISTER";
    public const string ZkDiscover = "ZK_DISCOVER";
    public const string LinkUp = "LINK_UP";
    public const string LinksReady = "LINKS_READY";
    public const string LinksLost = "LINKS_LOST";
    public const string UnmatchedReply = "UNMATCHED_REPLY";
}

public static class ServerLogFields
{
    public const string Time = "time";
    public const string Level = "level";
    public const string Event = "ev";
    public const string Role = "role";
    public const string Instance = "instance";
    public const string Pid = "pid";
    public const string Peer = "peer";
    public const string Id = "id";
    public const string Entity = "entity";
    public const string Link = "link";
    public const string Detail = "detail";
}
