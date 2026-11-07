using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TGame.ServerConsole;

internal enum ConsoleAction
{
    Start,
    Stop,
    ExportProtobuf,
    Build,
    BuildAndRestart
}

internal sealed record ConsoleProject(string EnvironmentName, IReadOnlyList<ConsoleAction> Commands)
{
    public static ConsoleProject Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter<ConsoleAction>(allowIntegerValues: false) }
        };
        try
        {
            var project = JsonSerializer.Deserialize<ConsoleProject>(File.ReadAllText(path), options);
            if (project is null || string.IsNullOrWhiteSpace(project.EnvironmentName) ||
                project.Commands is not { Count: > 0 } || project.Commands.Distinct().Count() != project.Commands.Count)
            {
                throw new InvalidDataException($"{path} 需要环境名称和不重复的命令列表。");
            }

            return project;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"项目配置 {path} 有误。{ex.Message}", ex);
        }
    }
}
