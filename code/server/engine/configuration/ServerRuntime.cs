using System.IO;

namespace TGame;

public static class ServerRuntime
{
    public const string FileName = YamlConfiguration.FileName;
    public static string ConfigurationPath => YamlConfiguration.ConfigurationPath;

    public static string LoadDirectory(string path, string workingDirectory) =>
        new YamlConfiguration(path).ResolveRunDirectory(workingDirectory);

    public static void Save(string path, string directory, string workingDirectory)
    {
        var relative = Path.GetRelativePath(workingDirectory, directory);
        new YamlConfiguration(path).SaveSnapshot(path, relative);
    }
}
