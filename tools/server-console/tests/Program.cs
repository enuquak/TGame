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
    return 0;
}

LogChecks.Run(checkout);
var session = new ConsoleSession(checkout);
session.Output += Console.WriteLine;
try
{
    await session.ExportAsync();
    string[] generatedFiles = ["code/server/engine/generated/protocols/Transport.cs",
        "code/server/engine/generated/protocols/TransportGrpc.cs", "code/shared/generated/protocols/Profile.cs"];
    foreach (var generatedFile in generatedFiles)
    {
        if (!File.Exists(Path.Combine(checkout, generatedFile)))
        {
            throw new InvalidOperationException($"Protobuf export did not generate {generatedFile}.");
        }
    }

    if (args.Length == 2 && args[1] == "--export-only")
    {
        Console.WriteLine("PASS engine and business Protobuf export.");
        return 0;
    }

    await session.StartAsync();
    await UntilAsync("all SinglePoints ready", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    await CheckDuplicateRegistrationAsync();
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
    var originalConfig = Environment.GetEnvironmentVariable(ServerEnvironment.Config);
    try
    {
        Environment.SetEnvironmentVariable(ServerEnvironment.Config, Path.Combine(checkout, "missing-new-environment.yaml"));
        session.Dispose();
        session = new ConsoleSession(checkout);
        session.Output += Console.WriteLine;
        selected = session.Instances.Last(i => i.Role == nameof(ProcessType.Base));
        await session.StopAsync([selected]);
        await UntilAsync("Base stopped, Gate still running with a missing link", () =>
            selected.Status == ServerStatus.Stopped &&
            session.Instances.Single(i => i.Name == ServerProcess.FormatName(nameof(ProcessType.Gate), 1)).Status == ServerStatus.Disconnected);
        await session.StartAsync();
        await UntilAsync("Base rejoined with the original run configuration", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    }
    finally
    {
        Environment.SetEnvironmentVariable(ServerEnvironment.Config, originalConfig);
    }

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
    var name = ServerProcess.FormatName(role, 1);
    var router = session.Instances.Single(i => i.Name == name);
    using var original = Process.GetProcessById(router.Pid!.Value);
    var directory = Directory.CreateDirectory(Path.Combine(checkout, "duplicate-registration")).FullName;
    File.Copy(Path.Combine(checkout, ServerGroup.ConfigurationPath), Path.Combine(directory, ServerGroup.FileName), true);
    foreach (bool hasSessionMarker in new[] { false, true })
    {
        if (hasSessionMarker)
        {
            File.WriteAllText(Path.Combine(directory, ConsoleSession.SessionMarker), "");
        }

        var info = new ProcessStartInfo(original.MainModule!.FileName)
        {
            WorkingDirectory = checkout,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(directory);
        info.Environment[EngineEnvironment.LogRoot] = Path.Combine(directory, "logs");
        info.Environment[ServerEnvironment.Config] = Path.Combine(Path.GetDirectoryName(original.MainModule!.FileName)!, "config", "server.local.yaml");

        using var duplicate = Process.Start(info)!;
        var error = duplicate.StandardError.ReadToEndAsync();
        try
        {
            await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(DuplicateExitTimeoutSeconds));
            using var failure = JsonDocument.Parse(await error);
            var detail = failure.RootElement.GetProperty(ServerLogFields.Detail).GetString();
            if (duplicate.ExitCode != 1 ||
                failure.RootElement.GetProperty(ServerLogFields.Event).GetString() != ServerLogEvents.ProcessFailed ||
                detail?.Contains($"ZooKeeper node '/tgame/test/server-{name}' already exists.") != true)
            {
                throw new InvalidOperationException("Duplicate startup must report the occupied path and exit with code 1 regardless of the console session marker.");
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

    Console.WriteLine("PASS duplicate Router startup fails with or without a console session marker without disturbing the owner.");
}
