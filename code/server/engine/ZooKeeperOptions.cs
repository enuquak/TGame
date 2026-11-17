using System.Text.Json;

namespace TGame.Server.Engine;

public sealed record ZooKeeperOptions
{
    public const string FileName = "zookeeper.json";

    public string Endpoint { get; init; } = "";
    public string DiscoveryRoot { get; init; } = "";
    public required int SessionTimeoutMilliseconds { get; init; }
    public required int ConnectTimeoutMilliseconds { get; init; }

    public static ZooKeeperOptions Load(string path, string endpoint, string discoveryRoot)
    {
        var configured = JsonSerializer.Deserialize<ZooKeeperOptions>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing ZooKeeper configuration.");
        var options = configured with { Endpoint = endpoint, DiscoveryRoot = discoveryRoot };
        options.Validate();
        return options;
    }

    internal void Validate()
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
