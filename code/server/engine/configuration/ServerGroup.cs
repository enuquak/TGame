using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text.Json;

namespace TGame;

public sealed record ServerProcess(ProcessType Type, int ChannelId)
{
    public string Role => Type.ToString();
    public string Name => FormatName(Role, ChannelId);

    public static string FormatName(string role, int channelId)
    {
        if (channelId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channelId));
        }

        return role + channelId.ToString(CultureInfo.InvariantCulture);
    }

    public static ProcessType ParseType(string role)
    {
        if (!Enum.TryParse<ProcessType>(role, out var type) || !Enum.IsDefined(type) || type.ToString() != role)
        {
            throw new InvalidDataException($"Unknown process type {role}.");
        }

        return type;
    }

    public static ServerProcess Parse(string name)
    {
        if (!TryParse(name, out var process))
        {
            throw new InvalidDataException($"Invalid process instance {name}.");
        }

        return process;
    }

    public static bool TryParse(string name, out ServerProcess process)
    {
        process = null!;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        int start = name.Length;
        while (start > 0 && char.IsAsciiDigit(name[start - 1]))
        {
            start--;
        }

        var role = name[..start];
        if (!Enum.TryParse<ProcessType>(role, out var type) ||
            !Enum.IsDefined(type) ||
            type.ToString() != role ||
            !int.TryParse(name.AsSpan(start), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
            number < 1 ||
            FormatName(role, number) != name)
        {
            return false;
        }

        process = new ServerProcess(type, number);
        return true;
    }
}

public sealed class ServerGroup
{
    public const string FileName = ServerRunFiles.GroupConfiguration;
    public static string ConfigurationPath => Path.Combine("code", "server", "config", FileName);

    private readonly Dictionary<string, int> configuredCounts = new(StringComparer.Ordinal);
    public IReadOnlyList<ServerProcess> Processes { get; }

    public int GetStartupMinimum(ProcessType type) => configuredCounts[type.ToString()];

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
            }
        }

        if (processes.Count == 0)
        {
            throw new InvalidDataException("The server group must contain at least one process.");
        }

        Processes = processes.AsReadOnly();
    }

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

                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out int count) ||
                    count < 0 ||
                    !counts.TryAdd(type, count))
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
