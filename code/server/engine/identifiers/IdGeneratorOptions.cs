using System.Text.Json;

namespace TGame.Server.Engine;

public sealed record IdGeneratorOptions
{
    public const string FileName = "id-generator.json";

    public required string WorkerRootPath { get; init; }

    public static IdGeneratorOptions Load(string path)
    {
        var options = JsonSerializer.Deserialize<IdGeneratorOptions>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing ID generator configuration.");
        options.Validate();
        return options;
    }

    internal void Validate() => ZooKeeperOptions.ValidatePath(WorkerRootPath);
}
