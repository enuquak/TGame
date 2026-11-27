using System;

namespace TGame.Server.Engine;

public sealed record ZooKeeperOptions
{
    public const string SectionName = "zooKeeperOptions";
    public const string FileName = ServerConfiguration.FileName;

    public string Endpoint { get; init; } = "";
    public string DiscoveryRoot { get; init; } = "";
    public string WorkerRootPath { get; init; } = "";
    public required int SessionTimeoutMilliseconds { get; init; }
    public required int ConnectTimeoutMilliseconds { get; init; }

    public static ZooKeeperOptions Load(string path)
    {
        return new ServerConfiguration(path).ZooKeeperOptions;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint);
        ZooKeeperPath.ValidateRoot(DiscoveryRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SessionTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConnectTimeoutMilliseconds);
    }
}
