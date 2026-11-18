using System.Diagnostics;
using System.Text.Json;
using TGame;
using TGame.ServerConsole;

internal static class DynamicIdentityChecks
{
    public static async Task RunAsync()
    {
        await CheckAsync(dynamicIdentity: true);
        await CheckAsync(dynamicIdentity: false);
    }

    private static async Task CheckAsync(bool dynamicIdentity)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tgame-identity-" + Guid.NewGuid().ToString("N"))).FullName;
        var executable = Path.Combine(root, "Router1Server.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "sort.exe"), executable);
        using var child = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        })!;
        try
        {
            long ticks = child.StartTime.ToUniversalTime().Ticks;
            File.WriteAllText(Path.Combine(root, "Router1.json"), JsonSerializer.Serialize(new { Pid = child.Id, StartedUtcTicks = ticks }));
            var key = ServerRunFiles.ProcessKey(child.Id, ticks);
            var assignedName = dynamicIdentity ? "Router7" : "Router1";
            if (dynamicIdentity)
            {
                File.WriteAllText(Path.Combine(root, ServerRunFiles.IdentityPrefix + key), assignedName);
            }

            var logs = Directory.CreateDirectory(Path.Combine(root, assignedName + "Server")).FullName;
            var rows = new[] { ServerLogEvents.LinksReady, ServerLogEvents.ProcessReady }.Select(ev =>
                JsonSerializer.Serialize(new { pid = child.Id, role = nameof(ProcessType.Router), instance = assignedName, ev }));
            File.WriteAllLines(Path.Combine(logs, key + "-2026111812.log"), rows);
            using var instance = new ServerInstance(nameof(ProcessType.Router));
            instance.Open(root, root, root);
            if (instance.Name != assignedName || !instance.Alive || instance.Status != ServerStatus.Ready)
            {
                throw new Exception("Console must follow the claimed identity independently of its launch slot.");
            }

            var stopping = instance.StopAsync(Console.WriteLine);
            var marker = dynamicIdentity ? ServerRunFiles.StopProcessPrefix + key : ServerRunFiles.StopRolePrefix + assignedName;
            if (!File.Exists(Path.Combine(root, marker)))
            {
                throw new Exception("Stop must address the exact process lifetime, not a reusable instance number.");
            }

            child.StandardInput.Close();
            await stopping;
            Console.WriteLine($"PASS console identity, logs and stop protocol (dynamic={dynamicIdentity}).");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
        }
    }
}
