using System.Diagnostics;
using System.Text.Json;
using TGame;
using TGame.ServerConsole;

if (args is ["--identity-only"] or ["--state-only"])
{
    try
    {
        if (args[0] == "--state-only")
        {
            var root = Directory.CreateTempSubdirectory("tgame-console-state-").FullName;
            LogChecks.Run(root);
        }

        await DynamicIdentityChecks.RunAsync();
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

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
    await UntilAsync("all processes ready", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    await CheckDynamicRegistrationAsync();
    var pids = session.Instances.ToDictionary(i => i.SlotName, i => i.Pid);
    using (var reopened = new ConsoleSession(checkout))
    {
        reopened.Refresh();
        if (reopened.Instances.Any(i => i.Status != ServerStatus.Ready || pids[i.SlotName] != i.Pid))
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
            session.Instances.Single(i => i.Name == ServerProcess.FormatName(nameof(ProcessType.Gate), 1)).Status ==
                (session.Instances.Count(i => i.Role == nameof(ProcessType.Base)) > 1 ? ServerStatus.Ready : ServerStatus.Disconnected));
        await session.StartAsync();
        await UntilAsync("Base rejoined with the original run configuration", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    }
    finally
    {
        Environment.SetEnvironmentVariable(ServerEnvironment.Config, originalConfig);
    }

    if (selected.Pid == pids[selected.SlotName] || session.Instances.Any(i => i.Name != selected.Name && i.Pid != pids[i.SlotName]))
    {
        throw new InvalidOperationException("Restoring Base disturbed another instance.");
    }

    var router = session.Instances.Last(i => i.Role == nameof(ProcessType.Router));
    var routerPid = router.Pid;
    await session.StopAsync([router]);
    await UntilAsync("Router stopped and every Base has an incomplete topology", () =>
        router.Status == ServerStatus.Stopped &&
        session.Instances.Where(i => i.Role == nameof(ProcessType.Base)).All(i => i.Status ==
            (session.Instances.Count(i => i.Role == nameof(ProcessType.Router)) > 1 ? ServerStatus.Ready : ServerStatus.Disconnected)));
    await session.StartAsync();
    await UntilAsync("Router rejoined", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    if (router.Pid == routerPid)
    {
        throw new InvalidOperationException("Restarting the Router instance retained its old PID.");
    }

    // A compiler error must leave the running group and its PID associations intact.
    var broken = Path.Combine(checkout, "code/server/app/BrokenCheck.cs");
    await File.WriteAllTextAsync(broken, "#error ServerConsole.Check");
    pids = session.Instances.ToDictionary(i => i.SlotName, i => i.Pid);
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
    if (!rejected || session.Instances.Any(i => i.Status != ServerStatus.Ready || i.Pid != pids[i.SlotName]))
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
        (!pids.TryGetValue(i.SlotName, out var oldPid) || i.Pid != oldPid)));
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

async Task CheckDynamicRegistrationAsync()
{
    const int StartupTimeoutSeconds = 10;
    var router = session.Instances.First(instance => instance.Role == nameof(ProcessType.Router));
    using var original = Process.GetProcessById(router.Pid!.Value);
    var runtimeConfig = Path.Combine(Path.GetDirectoryName(original.MainModule!.FileName)!, "config", ServerRuntime.FileName);
    var directory = ServerRuntime.LoadDirectory(runtimeConfig, checkout);
    var info = new ProcessStartInfo(original.MainModule!.FileName)
    {
        WorkingDirectory = checkout,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    info.ArgumentList.Add(nameof(ProcessType.Router));
    info.Environment[EngineEnvironment.LogRoot] = Path.Combine(checkout, "dynamic-registration", "logs");
    info.Environment[ServerEnvironment.Config] = Path.Combine(Path.GetDirectoryName(original.MainModule!.FileName)!, "config", "server.local.yaml");
    using var extra = Process.Start(info)!;
    try
    {
        var key = ServerRunFiles.ProcessKey(extra.Id, extra.StartTime.ToUniversalTime().Ticks);
        var identityPath = Path.Combine(directory, ServerRunFiles.IdentityPrefix + key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(StartupTimeoutSeconds));
        while (!File.Exists(identityPath))
        {
            if (extra.HasExited)
            {
                throw new InvalidOperationException("Additional Router failed before claiming an identity.");
            }

            await Task.Delay(20, timeout.Token);
        }

        var assigned = ServerProcess.Parse(File.ReadAllText(identityPath));
        if (assigned.Type != ProcessType.Router || session.Instances.Any(instance => instance.Name == assigned.Name))
        {
            throw new InvalidOperationException("Additional Router did not claim a free identity.");
        }

        File.WriteAllText(Path.Combine(directory, ServerRunFiles.StopProcessPrefix + key), "");
        await extra.WaitForExitAsync(timeout.Token);
        if (extra.ExitCode != 0 || original.HasExited)
        {
            throw new InvalidOperationException("Additional Router disrupted an existing instance or could not stop cleanly.");
        }
    }
    finally
    {
        if (!extra.HasExited)
        {
            extra.Kill();
            await extra.WaitForExitAsync();
        }
    }

    Console.WriteLine("PASS dynamic Router startup claims a free number without a process-count file.");
}
