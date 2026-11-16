using System.Diagnostics;
using System.Text.Json;
using TGame;
using TGame.ServerConsole;

// Run against a disposable checkout so a developer's live servers are never stopped.
if (args is not [var checkout, ..] || args.Length > 2 || !File.Exists(Path.Combine(checkout, ".console-check")))
{
    throw new ArgumentException("Expected a disposable checkout created by Test-ServerConsole.ps1.");
}

ConfigChecks.Run(checkout);
GroupConfigChecks.Run(checkout);
if (args.Length == 2 && args[1] == "--config-only")
{
    return;
}

LogChecks.Run(checkout);
using var session = new ConsoleSession(checkout);
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
    var pids = session.Instances.ToDictionary(i => i.Name, i => i.Pid);
    using (var reopened = new ConsoleSession(checkout))
    {
        reopened.Refresh();
        if (reopened.Instances.Any(i => i.Status != ServerStatus.Ready || pids[i.Name] != i.Pid))
        {
            throw new InvalidOperationException("Reopening the console lost the active instance or its status.");
        }
    }

    var selected = session.Instances.Last(i => i.Role == nameof(ProcessType.Base));
    await session.StopAsync([selected]);
    await UntilAsync("Base stopped, Gate still running with a missing link", () =>
        selected.Status == ServerStatus.Stopped &&
        session.Instances.Single(i => i.Name == ServerProcess.FormatName(nameof(ProcessType.Gate), 1)).Status == ServerStatus.Disconnected);
    await session.StartAsync();
    await UntilAsync("Base rejoined", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    if (selected.Pid == pids[selected.Name] || session.Instances.Any(i => i.Name != selected.Name && i.Pid != pids[i.Name]))
    {
        throw new InvalidOperationException("Restoring Base disturbed another instance.");
    }

    var router = session.Instances.Last(i => i.Role == nameof(ProcessType.Router));
    var routerPid = router.Pid;
    await session.StopAsync([router]);
    await UntilAsync("Router stopped and every Base has an incomplete topology", () =>
        router.Status == ServerStatus.Stopped &&
        session.Instances.Where(i => i.Role == nameof(ProcessType.Base)).All(i => i.Status == ServerStatus.Disconnected));
    await session.StartAsync();
    await UntilAsync("Router rejoined", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    if (router.Pid == routerPid)
    {
        throw new InvalidOperationException("Restarting the Router instance retained its old PID.");
    }

    var gm = session.Instances.Single(i => i.Name == ServerProcess.FormatName(nameof(ProcessType.GM), 1));
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

    var dbMgr = session.Instances.Single(i => i.Name == ServerProcess.FormatName(nameof(ProcessType.DBMgr), 1));
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
    pids = session.Instances.ToDictionary(i => i.Name, i => i.Pid);
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
    if (!rejected || session.Instances.Any(i => i.Status != ServerStatus.Ready || i.Pid != pids[i.Name]))
    {
        throw new InvalidOperationException("A failed build disrupted the running group.");
    }

    var configuration = Path.Combine(checkout, ServerGroup.ConfigurationPath);
    var counts = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(configuration))!;
    int expectedInstances = session.Instances.Count + 1;
    counts[nameof(ProcessType.Login)]++;
    File.WriteAllText(configuration, JsonSerializer.Serialize(counts));
    bool instancesChanged = false;
    session.InstancesChanged += () => instancesChanged = true;

    await session.BuildAsync(restart: true);
    await UntilAsync("new group ready", () => session.Instances.All(i => i.Status == ServerStatus.Ready &&
        (!pids.TryGetValue(i.Name, out var oldPid) || i.Pid != oldPid)));
    if (!instancesChanged || session.Instances.Count != expectedInstances)
    {
        throw new InvalidOperationException("Starting a new group did not apply the edited process counts.");
    }

    using (var reopened = new ConsoleSession(checkout))
    {
        reopened.Refresh();
        if (reopened.Instances.Count != expectedInstances || reopened.Instances.Any(i => i.Status != ServerStatus.Ready))
        {
            throw new InvalidOperationException("Reopening lost the resized group's configuration snapshot.");
        }
    }

    Console.WriteLine($"PASS {session.Instances.Count} instances, export, selective stop, recovery, attach and build/restart.");
}
finally
{
    await session.StopAsync(session.Instances.ToArray());
}

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
