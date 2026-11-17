namespace TGame;

public static class ServerLogEvents
{
    public const string EntityRpcFailed = "ENTITY_RPC_FAILED";
    public const string LegacyReady = "READY";
    public const string LegacyBaseSinglePointInit = "BASE_SINGLE_POINT_INIT";
    public const string LegacyBaseSinglePointReady = "BASE_SINGLE_POINT_READY";
    public const string Listen = "LISTEN";
    public const string ProcessFailed = "PROCESS_FAILED";
    public const string SinglePointInit = "SINGLE_POINT_INIT";
    public const string SinglePointReady = "SINGLE_POINT_READY";
    public const string SinglePointFailed = "SINGLE_POINT_FAILED";
    public const string ZkRegister = "ZK_REGISTER";
    public const string ZkDiscover = "ZK_DISCOVER";
    public const string EntityRoute = "ENTITY_ROUTE";
    public const string LinkUp = "LINK_UP";
    public const string LinksReady = "LINKS_READY";
    public const string LinksLost = "LINKS_LOST";
    public const string UnmatchedReply = "UNMATCHED_REPLY";
}

public static class ServerLogFields
{
    public const string Time = "time";
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
