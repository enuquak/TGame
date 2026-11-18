using System.Text.Json;
using TGame;
using TGame.ServerConsole;

internal static class GroupConfigChecks
{
    public static void Run(string checkout)
    {
        var root = Path.Combine(checkout, "build", "group-config-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "code/server/config"));
        Directory.CreateDirectory(Path.Combine(root, "tools/server-console"));
        File.Copy(Path.Combine(checkout, "tools/server-console/project.json"),
            Path.Combine(root, "tools/server-console/project.json"), overwrite: true);

        var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type.ToString(), _ => 1);
        counts[nameof(ProcessType.Login)] = 2;
        counts[nameof(ProcessType.Base)] = 3;
        File.WriteAllText(Path.Combine(root, "code/server/config/processes.json"), JsonSerializer.Serialize(counts));

        using var session = new ConsoleSession(root);
        if (session.Instances.Count != 11 ||
            session.Instances.Select(item => item.Name).Distinct().Count() != 11 ||
            session.Instances.Count(item => item.Role == nameof(ProcessType.Login)) != 2 ||
            session.Instances.Count(item => item.Role == nameof(ProcessType.Base)) != 3)
        {
            throw new Exception("The console did not expand the configured instance counts.");
        }

        var groupFile = Path.Combine(root, ServerGroup.ConfigurationPath);
        var group = ServerGroup.Load(groupFile);
        var sessionName = Guid.NewGuid().ToString("N");
        var sessions = Path.Combine(root, "logs/tools/server-console");
        var run = Directory.CreateDirectory(Path.Combine(sessions, sessionName)).FullName;
        File.WriteAllText(Path.Combine(run, ConsoleSession.SessionMarker), "");
        File.WriteAllText(Path.Combine(run, ServerRunFiles.DynamicDiscoveryMarker), "");
        File.WriteAllText(Path.Combine(sessions, "current"), sessionName);
        group.Save(Path.Combine(run, ServerGroup.FileName));
        counts[nameof(ProcessType.Login)] = 1;
        counts[nameof(ProcessType.Base)] = 1;
        File.WriteAllText(groupFile, JsonSerializer.Serialize(counts));
        using var restored = new ConsoleSession(root);
        if (restored.Instances.Count != 11)
        {
            throw new Exception("Restoring a running group used edited source counts instead of its snapshot.");
        }

        var valid = JsonSerializer.Serialize(counts);
        string[] invalid =
        [
            "[]", "{}", "{bad", valid.Replace(":1", ":-1"), valid.Replace(":1", ":1.5"),
            valid.Replace(":1", ":0"), valid.Replace(nameof(ProcessType.Login), "Unknown"),
            valid[..^1] + $",\"{nameof(ProcessType.Login)}\":1}}"
        ];
        foreach (var json in invalid)
        {
            File.WriteAllText(groupFile, json);
            try
            {
                ServerGroup.Load(groupFile);
                throw new Exception("Accepted invalid counts, unknown types or duplicate configuration keys.");
            }
            catch (InvalidDataException)
            {
            }
        }

        File.WriteAllText(groupFile, valid);

        File.Delete(Path.Combine(run, ServerRunFiles.DynamicDiscoveryMarker));
        try
        {
            using var unsupported = new ConsoleSession(root);
            throw new Exception("Accepted a run without the current identity format marker.");
        }
        catch (InvalidDataException)
        {
        }

        Console.WriteLine("PASS configured 2 Login and 3 Base instances.");
    }
}
