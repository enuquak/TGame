using TGame;

internal static class ConfigurationFixture
{
    // 测试从独立的合法输入开始，只改当前用例需要覆盖的字段。
    public static Dictionary<string, object> CreateDocument() => new()
    {
        ["environment"] = "configuration-check",
        ["runDirectory"] = "logs/server-runs",
        ["loggingMinimumLevel"] = "DEBUG",
        ["zooKeeperOptions"] = new Dictionary<string, object>
        {
            ["endpoint"] = "127.0.0.1:2181",
            ["discoveryRoot"] = "/configuration-check",
            ["workerRootPath"] = "/configuration-workers",
            ["sessionTimeoutMilliseconds"] = 10000,
            ["connectTimeoutMilliseconds"] = 10000
        },
        [ServerGroup.SectionName] = Enum.GetValues<ProcessType>().ToDictionary(type => type.ToString(), _ => 1),
        [ServerConfiguration.SectionName] = Enum.GetValues<ProcessType>().ToDictionary(type => type.ToString(), _ => Array.Empty<string>())
    };
}
