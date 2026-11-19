using System;

namespace TGame.Server.Engine;

public sealed record ZooKeeperOptions
{
    public const string SectionName = "zooKeeper";
    public const string FileName = ServerConfiguration.FileName;

    public string Endpoint { get; init; } = "";
    public string DiscoveryRoot { get; init; } = "";
    public string WorkerRootPath { get; init; } = "";
    public required int SessionTimeoutMilliseconds { get; init; }
    public required int ConnectTimeoutMilliseconds { get; init; }

    public static ZooKeeperOptions Load(string path)
    {
        return new ServerConfiguration(path).ZooKeeperSettings;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint);
        ValidatePath(DiscoveryRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SessionTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConnectTimeoutMilliseconds);
    }

    internal static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/') || path.EndsWith('/'))
        {
            throw new ArgumentException("ZooKeeper path must be absolute and have no trailing slash.");
        }

        foreach (var segment in path[1..].Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException("Invalid ZooKeeper path segment.");
            }
        }
    }
}
