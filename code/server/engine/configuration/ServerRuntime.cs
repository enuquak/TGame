using System.IO;

namespace TGame;

public static class ServerRuntime
{
    public const string FileName = ServerConfiguration.FileName;
    public static string ConfigurationPath => ServerConfiguration.ConfigurationPath;

    public static string LoadDirectory(string path, string workingDirectory) =>
        new ServerConfiguration(path).ResolveRunDirectory(workingDirectory);

    public static void Save(string path, string directory, string workingDirectory)
    {
        var relative = Path.GetRelativePath(workingDirectory, directory);
        new ServerConfiguration(path).SaveSnapshot(path, relative);
    }
}
