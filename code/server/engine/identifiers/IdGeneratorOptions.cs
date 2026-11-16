using System.Text.Json;

namespace TGame.Server.Engine;

public sealed record IdGeneratorOptions
{
    public const string FileName = "id-generator.json";

    public string ZooKeeperEndpoint { get; init; } = "";
    public required string WorkerRootPath { get; init; }
    public required int SessionTimeoutMilliseconds { get; init; }
    public required int ConnectTimeoutMilliseconds { get; init; }

    public static IdGeneratorOptions Load(string path, string zooKeeperEndpoint)
    {
        var configured = JsonSerializer.Deserialize<IdGeneratorOptions>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing ID generator configuration.");
        var options = configured with { ZooKeeperEndpoint = zooKeeperEndpoint };
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ZooKeeperEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(WorkerRootPath);
        if (!WorkerRootPath.StartsWith('/') || WorkerRootPath.EndsWith('/'))
        {
            throw new ArgumentException("WorkerRootPath must be absolute and have no trailing slash.");
        }

        foreach (var segment in WorkerRootPath[1..].Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException("Invalid WorkerRootPath segment.");
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SessionTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConnectTimeoutMilliseconds);
    }
}
