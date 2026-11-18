namespace TGame.Server.Engine;

public sealed record IdGeneratorOptions
{
    public const string FileName = YamlConfiguration.FileName;

    public required string WorkerRootPath { get; init; }

    public static IdGeneratorOptions Load(string path)
    {
        var zooKeeper = ZooKeeperOptions.Load(path);
        var options = new IdGeneratorOptions { WorkerRootPath = zooKeeper.WorkerRootPath };
        options.Validate();
        return options;
    }

    public void Validate() => ZooKeeperOptions.ValidatePath(WorkerRootPath);
}
