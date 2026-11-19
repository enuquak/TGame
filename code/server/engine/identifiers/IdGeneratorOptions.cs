namespace TGame.Server.Engine;

public sealed record IdGeneratorOptions
{
    public const string FileName = ServerConfiguration.FileName;

    public required string WorkerRootPath { get; init; }

    public static IdGeneratorOptions Load(string path)
    {
        return new ServerConfiguration(path).Identifiers;
    }

    public void Validate() => ZooKeeperOptions.ValidatePath(WorkerRootPath);
}
