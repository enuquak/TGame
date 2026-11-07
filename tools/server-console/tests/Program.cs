using System.Diagnostics;
using TGame.ServerConsole;

// Run against a disposable checkout so a developer's live servers are never stopped.
if (args is not [var checkout] || !File.Exists(Path.Combine(checkout, ".console-check")))
    throw new ArgumentException("Expected a disposable checkout created by Test-ServerConsole.ps1.");
ConfigChecks.Run(checkout);
LogChecks.Run(checkout);
using var session = new ConsoleSession(checkout);
session.Output += Console.WriteLine;
try
{
    await session.ExportAsync();
    if (!Directory.EnumerateFiles(Path.Combine(checkout, "code/server/generated"), "*.cs", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException("Protobuf export produced no C# sources.");
    await session.StartAsync();
    await UntilAsync("all SinglePoints ready", () => session.Instances.All(i => i.Status == "就绪"));
    var pids = session.Instances.ToDictionary(i => i.Role, i => i.Pid);
    using (var reopened = new ConsoleSession(checkout))
    {
        reopened.Refresh();
        if (reopened.Instances.Any(i => i.Status != "就绪" || pids[i.Role] != i.Pid))
            throw new InvalidOperationException("Reopening the console lost the active instance or its status.");
    }

    var selected = session.Instances.Single(i => i.Role == "Base");
    await session.StopAsync([selected]);
    await UntilAsync("Base stopped, Gate still running with a missing link", () =>
        selected.Status == "已停止" && session.Instances.Single(i => i.Role == "Gate").Status == "连接不全");
    await session.StartAsync();
    await UntilAsync("Base rejoined", () => session.Instances.All(i => i.Status == "就绪"));
    if (selected.Pid == pids["Base"] || session.Instances.Any(i => i.Role != "Base" && i.Pid != pids[i.Role]))
        throw new InvalidOperationException("Restoring Base disturbed another instance.");

    var gm = session.Instances.Single(i => i.Role == "GM");
    await session.StopAsync([gm]);
    var originalToken = Environment.GetEnvironmentVariable("TGAME_GM_TOKEN");
    try
    {
        Environment.SetEnvironmentVariable("TGAME_GM_TOKEN", "invalid");
        await session.StartAsync();
        await UntilAsync("GM role initialization failed", () => gm.Status == "异常退出" && !gm.Alive);
        if (!HasEvent(gm, "LINKS_READY") || !HasEvent(gm, "SINGLE_POINT_FAILED") || HasEvent(gm, "SINGLE_POINT_READY"))
            throw new InvalidOperationException("GM reported readiness despite an invalid credential.");
    }
    finally { Environment.SetEnvironmentVariable("TGAME_GM_TOKEN", originalToken); }
    await session.StartAsync();
    await UntilAsync("GM role initialization recovered", () => session.Instances.All(i => i.Status == "就绪"));

    // A compiler error must leave the running group and its PID associations intact.
    var broken = Path.Combine(checkout, "code/server/app/BrokenCheck.cs");
    await File.WriteAllTextAsync(broken, "#error ServerConsole.Check");
    pids = session.Instances.ToDictionary(i => i.Role, i => i.Pid);
    bool rejected = false;
    try { await session.BuildAsync(restart: true); }
    catch (Exception ex) { rejected = true; Console.WriteLine(ex.Message); }
    finally { File.Delete(broken); }
    session.Refresh();
    if (!rejected || session.Instances.Any(i => i.Status != "就绪" || i.Pid != pids[i.Role]))
        throw new InvalidOperationException("A failed build disrupted the running group.");
    await session.BuildAsync(restart: true);
    await UntilAsync("new group ready", () => session.Instances.All(i => i.Status == "就绪" && i.Pid != pids[i.Role]));
    Console.WriteLine($"PASS {session.Instances.Count} instances, export, selective stop, recovery, attach and build/restart.");
}
finally
{
    await session.StopAsync(session.Instances.ToArray());
}

async Task UntilAsync(string stage, Func<bool> condition)
{
    var elapsed = Stopwatch.StartNew();
    while (elapsed.Elapsed < TimeSpan.FromSeconds(75))
    {
        session.Refresh();
        if (condition()) return;
        if (session.Instances.Any(i => i.Status == "异常退出" && !i.Alive)) break;
        await Task.Delay(150);
    }
    foreach (var item in session.Instances)
        Console.Error.WriteLine($"{item.Role} {item.Pid} {item.Status} {item.Lines.LastOrDefault()}");
    throw new InvalidOperationException("Timed out or process failed: " + stage);
}

static bool HasEvent(ServerInstance instance, string name) =>
    instance.Lines.Any(line => line.Contains($"\"ev\":\"{name}\"", StringComparison.Ordinal));
