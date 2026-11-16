using TGame;
using TGame.ServerConsole;

internal static class ConfigChecks
{
    public static void Run(string checkout)
    {
        var folder = Directory.CreateDirectory(Path.Combine(checkout, "build", "config-check")).FullName;
        var projectFile = Path.Combine(folder, "project.json");
        File.WriteAllText(projectFile, $$"""{"environmentName":"联调环境","commands":["{{nameof(ConsoleAction.Build)}}","{{nameof(ConsoleAction.Start)}}"]}""");
        var project = ConsoleProject.Load(projectFile);
        if (project.EnvironmentName != "联调环境" || !project.Commands.SequenceEqual([ConsoleAction.Build, ConsoleAction.Start]))
        {
            throw new InvalidOperationException("Project command order or environment name was lost.");
        }

        File.WriteAllText(projectFile, """{"environmentName":"联调环境","commands":["EraseDatabase"]}""");
        try
        {
            ConsoleProject.Load(projectFile);
            throw new Exception("Accepted an unknown project command.");
        }
        catch (InvalidDataException)
        {
        }

        string first = Path.Combine(folder, "user-a", "user.json");
        string second = Path.Combine(folder, "user-b", "user.json");
        var settings = UserSettings.Load(first);
        var gmName = ServerProcess.FormatName(nameof(ProcessType.GM), 1);
        settings.SelectedRole = gmName;
        settings.Width = 1100;
        settings.Logs[gmName] = new("ping", true, false);
        settings.Save();
        var restored = UserSettings.Load(first);
        if (restored.SelectedRole != gmName ||
            restored.Width != 1100 ||
            restored.Logs[gmName] != new LogPreference("ping", true, false) ||
            UserSettings.Load(second).SelectedRole != null ||
            File.Exists(second))
        {
            throw new InvalidOperationException("User settings were lost or shared with a second profile.");
        }

        File.WriteAllText(first, "{bad");
        if (UserSettings.Load(first).Warning == null || File.ReadAllText(first) != "{bad")
        {
            throw new InvalidOperationException("Invalid user settings were silently accepted or overwritten on load.");
        }

        Console.WriteLine("PASS shared environment/command order and isolated user preferences.");
    }
}
