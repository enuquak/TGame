using System.Diagnostics;
using TGame;
using TGame.ServerConsole;

if (args is ["--identity-only"] or ["--state-only"])
{
    try
    {
        if (args[0] == "--state-only")
        {
            var root = Directory.CreateTempSubdirectory("tgame-console-state-").FullName;
            ConfigChecks.Run(root);
            PreferenceChecks.Run(root);
            GroupConfigChecks.Run(root);
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

// 集成检查只接受临时副本，避免停止开发者正在运行的服务器。
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

var selectedConfiguration = Environment.GetEnvironmentVariable(EngineEnvironment.Config);
if (string.IsNullOrWhiteSpace(selectedConfiguration))
{
    selectedConfiguration = ServerConfiguration.ConfigurationPath;
}

var fixtureRoot = Path.GetFullPath(checkout).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
var configuration = Path.GetFullPath(selectedConfiguration, fixtureRoot);
if (!configuration.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("The test configuration must be inside the disposable checkout.");
    return 1;
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
    session.Dispose();
    session = new ConsoleSession(checkout);
    session.Output += Console.WriteLine;
    selected = session.Instances.Last(i => i.Role == nameof(ProcessType.Base));
    await session.StopAsync([selected]);
    await UntilAsync("Base stopped and Gate preserves startup readiness", () =>
        selected.Status == ServerStatus.Stopped &&
        session.Instances.Single(i => i.Name == ServerProcess.FormatName(nameof(ProcessType.Gate), 1)).Status == ServerStatus.Ready);
    await session.StartAsync();
    await UntilAsync("Base rejoined", () => session.Instances.All(i => i.Status == ServerStatus.Ready));

    if (selected.Pid == pids[selected.SlotName] || session.Instances.Any(i => i.Name != selected.Name && i.Pid != pids[i.SlotName]))
    {
        throw new InvalidOperationException("Restoring Base disturbed another instance.");
    }

    var router = session.Instances.Last(i => i.Role == nameof(ProcessType.Router));
    var routerPid = router.Pid;
    await session.StopAsync([router]);
    await UntilAsync("Router stopped and every Base preserves startup readiness", () =>
        router.Status == ServerStatus.Stopped &&
        session.Instances.Where(i => i.Role == nameof(ProcessType.Base)).All(i => i.Status == ServerStatus.Ready));
    await session.StartAsync();
    await UntilAsync("Router rejoined", () => session.Instances.All(i => i.Status == ServerStatus.Ready));
    if (router.Pid == routerPid)
    {
        throw new InvalidOperationException("Restarting the Router instance retained its old PID.");
    }

    // 编译失败不得破坏正在运行的进程组及其 PID 关联。
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

    var loaded = new ServerConfiguration(configuration);
    var counts = Enum.GetValues<ProcessType>().ToDictionary(type => type.ToString(), loaded.Group.GetStartupMinimum);
    int expectedInstances = session.Instances.Count + 1;
    counts[nameof(ProcessType.Login)]++;
    GroupConfigChecks.UpdateCounts(configuration, counts);
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
            throw new InvalidOperationException("Reopening lost the resized group's process counts.");
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
    var sessions = Path.Combine(checkout, "logs", "tools", "server-console");
    var directory = Path.Combine(sessions, File.ReadAllText(Path.Combine(sessions, "current")).Trim());
    var info = new ProcessStartInfo(original.MainModule!.FileName)
    {
        WorkingDirectory = checkout,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    info.ArgumentList.Add(nameof(ProcessType.Router));
    info.Environment[EngineEnvironment.LogRoot] = Path.Combine(checkout, "dynamic-registration", "logs");
    info.Environment[EngineEnvironment.Config] = configuration;
    info.Environment[EngineEnvironment.RunDirectory] = directory;
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
