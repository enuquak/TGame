using System.Diagnostics;
using TGame;
using TGame.ServerConsole;

// Run against a disposable checkout so a developer's live servers are never stopped.
if (args is not [var checkout] || !File.Exists(Path.Combine(checkout, ".console-check")))
{
    throw new ArgumentException("Expected a disposable checkout created by Test-ServerConsole.ps1.");
}

ConfigChecks.Run(checkout);
LogChecks.Run(checkout);
var session = new ConsoleSession(checkout);
session.Output += Console.WriteLine;
try
{
    await session.ExportAsync();
    if (!Directory.EnumerateFiles(Path.Combine(checkout, "code/shared/generated"), "*.cs", SearchOption.AllDirectories).Any())
    {
        throw new InvalidOperationException("Protobuf export produced no C# sources.");
    }

    await session.StartAsync();
    await UntilAsync("all SinglePoints ready", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    await CheckDuplicateRegistrationAsync();
    var pids = session.Instances.ToDictionary(i => i.Role, i => i.Pid);
    using (var reopened = new ConsoleSession(checkout))
    {
        reopened.Refresh();
        if (reopened.Instances.Any(i => i.Status != ServerStatus.Ready || pids[i.Role] != i.Pid))
        {
            throw new InvalidOperationException("Reopening the console lost the active instance or its status.");
        }
    }

    var selected = session.Instances.Single(i => i.Role == nameof(ProcessType.Base));
    var originalConfig = Environment.GetEnvironmentVariable(ServerEnvironment.Config);
    try
    {
        Environment.SetEnvironmentVariable(ServerEnvironment.Config, Path.Combine(checkout, "missing-new-environment.yaml"));
        session.Dispose();
        session = new ConsoleSession(checkout);
        session.Output += Console.WriteLine;
        selected = session.Instances.Single(i => i.Role == nameof(ProcessType.Base));
        await session.StopAsync([selected]);
        await UntilAsync("Base stopped, Gate still running with a missing link", () =>
            selected.Status == ServerStatus.Stopped &&
            session.Instances.Single(i => i.Role == nameof(ProcessType.Gate)).Status == ServerStatus.Disconnected);
        await session.StartAsync();
        await UntilAsync("Base rejoined with the original run configuration", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    }
    finally
    {
        Environment.SetEnvironmentVariable(ServerEnvironment.Config, originalConfig);
    }

    if (selected.Pid == pids[nameof(ProcessType.Base)] || session.Instances.Any(i => i.Role != nameof(ProcessType.Base) && i.Pid != pids[i.Role]))
    {
        throw new InvalidOperationException("Restoring Base disturbed another instance.");
    }

    var gm = session.Instances.Single(i => i.Role == nameof(ProcessType.GM));
    await session.StopAsync([gm]);
    var originalToken = Environment.GetEnvironmentVariable(ServerEnvironment.GMToken);
    try
    {
        Environment.SetEnvironmentVariable(ServerEnvironment.GMToken, "invalid");
        await session.StartAsync();
        await UntilAsync("GM role initialization failed", () => gm.Status == ServerStatus.Failed && !gm.Alive);
        if (!HasEvent(gm, ServerLogEvents.LinksReady) || !HasEvent(gm, ServerLogEvents.SinglePointFailed) || HasEvent(gm, ServerLogEvents.SinglePointReady))
        {
            throw new InvalidOperationException("GM reported readiness despite an invalid credential.");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable(ServerEnvironment.GMToken, originalToken);
    }
    await session.StartAsync();
    await UntilAsync("GM role initialization recovered", () => session.Instances.All(i => i.Status == ServerStatus.Ready));

    var dbMgr = session.Instances.Single(i => i.Role == nameof(ProcessType.DBMgr));
    await session.StopAsync([dbMgr]);
    var originalMongoPassword = Environment.GetEnvironmentVariable(ServerEnvironment.DBMgrMongoPassword);
    try
    {
        Environment.SetEnvironmentVariable(ServerEnvironment.DBMgrMongoPassword, "invalid-storage-check-password");
        await session.StartAsync();
        await UntilAsync("DBMgr database initialization failed", () => dbMgr.Status == ServerStatus.Failed && !dbMgr.Alive);
        if (!HasEvent(dbMgr, ServerLogEvents.SinglePointFailed) || HasEvent(dbMgr, ServerLogEvents.SinglePointReady))
        {
            throw new InvalidOperationException("DBMgr reported readiness despite a failed database connection check.");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable(ServerEnvironment.DBMgrMongoPassword, originalMongoPassword);
    }

    await session.StartAsync();
    await UntilAsync("DBMgr database initialization recovered", () => session.Instances.All(i => i.Status == ServerStatus.Ready));

    // A compiler error must leave the running group and its PID associations intact.
    var broken = Path.Combine(checkout, "code/server/app/BrokenCheck.cs");
    await File.WriteAllTextAsync(broken, "#error ServerConsole.Check");
    pids = session.Instances.ToDictionary(i => i.Role, i => i.Pid);
    bool rejected = false;
    try
    {
        await session.BuildAsync(restart: true);
    }
    catch (Exception ex)
    {
        rejected = true;
        Console.WriteLine(ex.Message);
    }
    finally
    {
        File.Delete(broken);
    }

    session.Refresh();
    if (!rejected || session.Instances.Any(i => i.Status != ServerStatus.Ready || i.Pid != pids[i.Role]))
    {
        throw new InvalidOperationException("A failed build disrupted the running group.");
    }

    await session.BuildAsync(restart: true);
    await UntilAsync("new group ready", () => session.Instances.All(i => i.Status == ServerStatus.Ready && i.Pid != pids[i.Role]));
    Console.WriteLine($"PASS {session.Instances.Count} instances, export, selective stop, recovery, attach and build/restart.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    await session.StopAsync(session.Instances.ToArray());
    session.Dispose();
}

return 0;

async Task UntilAsync(string stage, Func<bool> condition)
{
    const int TimeoutSeconds = 75;
    const int PollIntervalMilliseconds = 150;
    var elapsed = Stopwatch.StartNew();
    while (elapsed.Elapsed < TimeSpan.FromSeconds(TimeoutSeconds))
    {
        session.Refresh();
        if (condition())
        {
            return;
        }

        if (session.Instances.Any(i => i.Status == ServerStatus.Failed && !i.Alive))
        {
            break;
        }

        await Task.Delay(PollIntervalMilliseconds);
    }

    foreach (var item in session.Instances)
    {
        Console.Error.WriteLine($"{item.Role} {item.Pid} {item.Status} {item.Lines.LastOrDefault()}");
    }

    throw new InvalidOperationException("Timed out or process failed: " + stage);
}

static bool HasEvent(ServerInstance instance, string name) =>
    instance.Lines.Any(line => line.Contains($"\"{ServerLogFields.Event}\":\"{name}\"", StringComparison.Ordinal));

async Task CheckDuplicateRegistrationAsync()
{
    const int DuplicateExitTimeoutSeconds = 10;
    var role = nameof(ProcessType.Router);
    var router = session.Instances.Single(i => i.Role == role);
    using var original = Process.GetProcessById(router.Pid!.Value);
    var directory = Directory.CreateDirectory(Path.Combine(checkout, "duplicate-registration")).FullName;
    foreach (bool consoleMode in new[] { false, true })
    {
        if (consoleMode)
        {
            File.WriteAllText(Path.Combine(directory, ServerRunFiles.ConsoleMode), "");
        }

        var info = new ProcessStartInfo(original.MainModule!.FileName)
        {
            WorkingDirectory = checkout,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(role);
        info.ArgumentList.Add(directory);
        info.Environment[ServerEnvironment.LogRoot] = Path.Combine(directory, "logs");
        info.Environment[ServerEnvironment.Config] = Path.Combine(Path.GetDirectoryName(original.MainModule!.FileName)!, "config", "server.local.yaml");

        using var duplicate = Process.Start(info)!;
        var error = duplicate.StandardError.ReadToEndAsync();
        try
        {
            await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(DuplicateExitTimeoutSeconds));
            if (duplicate.ExitCode != 1 || !(await error).Contains($"ZooKeeper node '/tgame/test/server-{role}' already exists."))
            {
                throw new InvalidOperationException("Duplicate startup must report the occupied path and exit with code 1 in both modes.");
            }
        }
        finally
        {
            if (!duplicate.HasExited)
            {
                duplicate.Kill();
                await duplicate.WaitForExitAsync();
            }
        }
    }

    session.Refresh();
    if (original.HasExited || router.Status != ServerStatus.Ready)
    {
        throw new InvalidOperationException("A rejected duplicate disrupted the existing Router.");
    }

    Console.WriteLine("PASS duplicate Router startup fails in console and non-console modes without disturbing the owner.");
}
