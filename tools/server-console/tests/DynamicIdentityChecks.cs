using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TGame;
using TGame.ServerConsole;

internal static class DynamicIdentityChecks
{
    public static async Task RunAsync()
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
            var started = child.StartTime;
            var fileName = started.Date.AddHours(started.Hour).ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture) + ".log";
            var startupDirectory = Directory.CreateDirectory(ServerRunFiles.StartupLogDirectory(root, child.Id, ticks)).FullName;
            var startupFile = Path.Combine(startupDirectory, fileName);
            var startupFailure = JsonSerializer.Serialize(new
            {
                pid = child.Id,
                role = nameof(ProcessType.Router),
                instance = nameof(ProcessType.Router),
                time = DateTimeOffset.UtcNow,
                ev = ServerLogEvents.ProcessFailed,
                detail = "ZooKeeper connection failed before claiming an identity"
            });
            File.WriteAllText(startupFile, startupFailure + "\n");
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var failed = new ServerInstance(nameof(ProcessType.Router));
                failed.Open(root, root, root);
                if (failed.Status != ServerStatus.Failed || !failed.Lines.Contains(startupFailure))
                {
                    throw new Exception("Console must show startup failures before identity assignment and after reopening.");
                }
            }

            File.Delete(startupFile);

            var assignedName = ServerProcess.FormatName(nameof(ProcessType.Router), 7);
            File.WriteAllText(Path.Combine(root, ServerRunFiles.IdentityPrefix + key), assignedName);

            var logs = Directory.CreateDirectory(Path.Combine(root, assignedName)).FullName;
            var ready = JsonSerializer.Serialize(new { pid = child.Id, role = nameof(ProcessType.Router), instance = assignedName, ev = ServerLogEvents.ProcessReady });
            File.WriteAllText(Path.Combine(logs, fileName), ready + "\n");
            using var instance = new ServerInstance(nameof(ProcessType.Router));
            instance.Open(root, root, root);
            if (instance.Name != assignedName || !instance.Alive || instance.Status != ServerStatus.Ready)
            {
                throw new Exception("Console must follow the claimed identity independently of its launch slot.");
            }

            var stopping = instance.StopAsync(Console.WriteLine);
            var marker = ServerRunFiles.StopProcessPrefix + key;
            if (!File.Exists(Path.Combine(root, marker)))
            {
                throw new Exception("Stop must address the exact process lifetime, not a reusable instance number.");
            }

            child.StandardInput.Close();
            await stopping;
            Console.WriteLine("PASS console identity, logs and stop protocol.");
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
