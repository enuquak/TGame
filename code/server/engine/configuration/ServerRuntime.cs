#nullable enable
using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;

namespace TGame;

public static class ServerRuntime
{
    public const string FileName = "runtime.json";
    private const string DirectoryKey = "runDirectory";
    public static string ConfigurationPath => Path.Combine("code", "server", "config", FileName);

    public static string LoadDirectory(string path, string workingDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        string? relative = null;
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Invalid runtime configuration: {path}.");
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name != DirectoryKey || relative is not null || property.Value.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Invalid runtime configuration: {path}.");
            }

            relative = property.Value.GetString();
        }

        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("runDirectory must be a nonempty relative path from the working directory.");
        }

        return Path.GetFullPath(relative, Path.GetFullPath(workingDirectory));
    }

    public static void Save(string path, string directory, string workingDirectory)
    {
        var relative = Path.GetRelativePath(workingDirectory, directory);
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("The runtime directory must be on the same drive as the working directory.");
        }

        var values = new Dictionary<string, string> { [DirectoryKey] = relative };
        File.WriteAllText(path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
    }
}
