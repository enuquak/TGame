using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TGame;

public sealed record ServerProcess(ProcessType Type, int Number)
{
    public string Role => Type.ToString();
    public string Name => FormatName(Role, Number);

    public static string FormatName(string role, int number)
    {
        if (number < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(number));
        }

        return number == 1 ? role : role + number;
    }
}

public sealed class ServerGroup
{
    public const string FileName = ServerRunFiles.GroupConfiguration;
    public static string ConfigurationPath => Path.Combine("code", "server", "config", FileName);

    private readonly Dictionary<string, ServerProcess> byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> configuredCounts = new(StringComparer.Ordinal);
    public IReadOnlyList<ServerProcess> Processes { get; }

    public ServerGroup(IReadOnlyDictionary<ProcessType, int> counts)
    {
        var processes = new List<ServerProcess>();
        var types = Enum.GetValues<ProcessType>();
        if (counts.Count != types.Length)
        {
            throw new InvalidDataException("Specify the count of every process type, including disabled types (0).");
        }

        foreach (var type in types)
        {
            if (!counts.TryGetValue(type, out int count) || count < 0)
            {
                throw new InvalidDataException($"Invalid or missing process count for {type}.");
            }

            configuredCounts.Add(type.ToString(), count);
            for (int index = 0; index < count; index++)
            {
                var process = new ServerProcess(type, index + 1);
                processes.Add(process);
                byName.Add(process.Name, process);
            }
        }

        if (processes.Count == 0)
        {
            throw new InvalidDataException("The server group must contain at least one process.");
        }

        Processes = processes.AsReadOnly();
    }

    public ServerProcess GetProcess(string name)
    {
        if (!byName.TryGetValue(name, out var process))
        {
            throw new InvalidDataException($"Process {name} is not configured in this server group.");
        }

        return process;
    }

    public bool Contains(string name) => byName.ContainsKey(name);

    public void Save(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(path, JsonSerializer.Serialize(configuredCounts, options));
    }

    public static ServerGroup Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{path} must map process types to instance counts.");
            }

            var counts = new Dictionary<ProcessType, int>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!Enum.TryParse<ProcessType>(property.Name, out var type) ||
                    !Enum.IsDefined(type) || type.ToString() != property.Name)
                {
                    throw new InvalidDataException($"Unknown process type {property.Name} in {path}.");
                }

                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int count) ||
                    count < 0 || !counts.TryAdd(type, count))
                {
                    throw new InvalidDataException($"Invalid or repeated count for {property.Name} in {path}.");
                }
            }

            return new ServerGroup(counts);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"Invalid server group configuration: {path}.", error);
        }
    }
}
