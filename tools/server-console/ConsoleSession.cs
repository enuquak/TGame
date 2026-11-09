using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TGame.ServerConsole;

internal sealed class ConsoleSession : IDisposable
{
    private const int GmTokenByteCount = 32;
    private const string Project = "code/server/app/TGame.Server.csproj";
    private const string ProtocolProject = "code/shared/TGame.Shared.csproj";
    private const string DefaultGmOperator = "local-console";
    private const string MongoPasswordKey = "MONGO_TEST_PASSWORD";
    private const string RedisPasswordKey = "REDIS_TEST_PASSWORD";

    private readonly string root, sessions, logRoot;
    private string? runId;
    private bool busy;
    public ConsoleProject ProjectSettings
    {
        get;
    }

    public IReadOnlyList<ServerInstance> Instances
    {
        get;
    }

    public event Action<string>? Output;

    public ConsoleSession(string repoRoot)
    {
        root = Path.GetFullPath(repoRoot);
        ProjectSettings = ConsoleProject.Load(Path.Combine(root, "tools", "server-console", "project.json"));
        sessions = Path.Combine(root, "logs", "tools", "server-console");
        logRoot = Path.Combine(root, "logs", "server");
        var roles = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(root, "code/server/config/processes.json")));
        if (roles is not { Length: > 0 } || roles.Any(r => string.IsNullOrEmpty(r) || !r.All(char.IsAsciiLetter)) ||
            roles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != roles.Length)
        {
            throw new InvalidDataException("processes.json 需要包含不重复的进程名。");
        }

        Instances = roles.Select(role => new ServerInstance(role)).ToArray();
        var current = Path.Combine(sessions, "current");
        if (!File.Exists(current))
        {
            return;
        }

        var saved = File.ReadAllText(current).Trim();
        if (!Guid.TryParseExact(saved, "N", out _) || !File.Exists(Path.Combine(sessions, saved, ServerRunFiles.ConsoleMode)))
        {
            throw new InvalidDataException("控制台当前会话记录无效。");
        }

        Open(saved);
    }

    public void Refresh()
    {
        foreach (var instance in Instances)
        {
            instance.Refresh();
        }
    }

    public Task StartAsync() => Command(async () =>
    {
        var missing = Instances.Where(i => !i.Alive).ToArray();
        if (missing.Length == 0)
        {
            Report("所有进程均已启动。");
            return;
        }

        if (missing.Length != Instances.Count)
        {
            Launch(missing, runId!);
            return;
        }

        await Compile();
        await StartNewRun();
    });

    public Task StopAsync(IReadOnlyList<ServerInstance> selected) => Command(() =>
        Task.WhenAll(Instances.Intersect(selected).Select(i => i.StopAsync(Report))));

    public Task BuildAsync(bool restart) => Command(async () =>
    {
        await Compile();
        if (restart)
        {
            await StartNewRun();
        }
    });

    public Task ExportAsync() => Command(() =>
        Dotnet("导出 Protobuf", "msbuild", ProtocolProject, "-restore", "-target:Protobuf_Compile", "-nologo", "-verbosity:minimal"));

    private Task Compile() => Dotnet("编译服务器", "build", Project, "--nologo");

    // The UI dispatches commands, timer ticks and continuations on the same thread.
    private async Task Command(Func<Task> action)
    {
        if (busy)
        {
            throw new InvalidOperationException("上一项操作仍在进行。");
        }

        busy = true;
        try
        {
            await action();
        }
        finally
        {
            busy = false;
        }
    }

    private string Snapshot(string id) => Path.Combine(root, "build", "server-console", "runs", id);

    private async Task StartNewRun()
    {
        var id = Guid.NewGuid().ToString("N");
        var snapshot = Snapshot(id);
        var runDir = Directory.CreateDirectory(Path.Combine(sessions, id)).FullName;
        File.WriteAllText(Path.Combine(runDir, ServerRunFiles.ConsoleMode), "");
        // Prepare credentials and a complete copy before asking healthy processes to shut down.
        var starts = Instances.Select(i => StartInfo(i, id)).ToArray();
        await Task.Run(() =>
        {
            var source = Path.Combine(root, "build", "bin", "TGame.Server", "debug");
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(snapshot, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }

            foreach (var start in starts)
            {
                File.Copy(Path.Combine(snapshot, "TGame.Server.exe"), start.FileName);
            }
        });
        await Task.WhenAll(Instances.Select(i => i.StopAsync(Report)));
        var previous = runId;
        var current = Path.Combine(sessions, "current");
        File.WriteAllText(current + ".tmp", id);
        File.Move(current + ".tmp", current, true);
        Open(id);
        for (int i = 0; i < Instances.Count; i++)
        {
            Launch(Instances[i], starts[i]);
        }

        if (previous != null && Directory.Exists(Snapshot(previous)))
        {
            try
            {
                Directory.Delete(Snapshot(previous), recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Report($"旧构建快照待清理：{e.Message}");
            }
        }
    }

    private void Open(string id)
    {
        runId = id;
        foreach (var instance in Instances)
        {
            instance.Open(Path.Combine(sessions, id), logRoot, Snapshot(id));
        }
    }

    private void Launch(ServerInstance[] missing, string id)
    {
        var starts = missing.Select(i => StartInfo(i, id)).ToArray();
        for (int i = 0; i < missing.Length; i++)
        {
            Launch(missing[i], starts[i]);
        }
    }

    private void Launch(ServerInstance instance, ProcessStartInfo start)
    {
        instance.Launch(start);
        Report($"已启动 {instance.Role} · PID {instance.Pid}");
    }

    private ProcessStartInfo StartInfo(ServerInstance instance, string id)
    {
        var info = NewProcess(Path.Combine(Snapshot(id), instance.ExecutableName));
        info.ArgumentList.Add(instance.Role);
        info.ArgumentList.Add(Path.Combine(sessions, id));
        info.Environment[ServerEnvironment.LogRoot] = logRoot;
        if (instance.Role is nameof(ProcessType.DBMgr) or nameof(ProcessType.Profile))
        {
            info.Environment[ServerEnvironment.MongoTestPassword] = Password(MongoPasswordKey);
        }

        if (instance.Role == nameof(ProcessType.Profile))
        {
            info.Environment[ServerEnvironment.RedisTestPassword] = Password(RedisPasswordKey);
        }

        if (instance.Role == nameof(ProcessType.GM))
        {
            var token = Environment.GetEnvironmentVariable(ServerEnvironment.GMToken);
            info.Environment[ServerEnvironment.GMToken] = string.IsNullOrEmpty(token)
                ? Convert.ToHexString(RandomNumberGenerator.GetBytes(GmTokenByteCount)) : token;
            var actor = Environment.GetEnvironmentVariable(ServerEnvironment.GMOperator);
            info.Environment[ServerEnvironment.GMOperator] = string.IsNullOrEmpty(actor) ? DefaultGmOperator : actor;
        }

        return info;
    }

    private string Password(string key)
    {
        var envKey = "TGAME_" + key;
        if (Environment.GetEnvironmentVariable(envKey) is { Length: > 0 } password)
        {
            return password;
        }

        var path = Path.Combine(root, ".local", "secrets", "cloud.env");
        var prefix = key + "=";
        if (File.Exists(path) && File.ReadLines(path).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) is { } line &&
            line.Length > prefix.Length)
        {
            return line[prefix.Length..];
        }

        throw new InvalidOperationException($"请设置 {envKey} 或在 .local/secrets/cloud.env 中配置 {key}。");
    }

    private ProcessStartInfo NewProcess(string exe)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        foreach (var key in new[] { ServerEnvironment.GMToken, ServerEnvironment.GMOperator, ServerEnvironment.MongoTestPassword,
            ServerEnvironment.RedisTestPassword, MongoPasswordKey, RedisPasswordKey })
        {
            info.Environment.Remove(key);
        }

        return info;
    }

    private async Task Dotnet(string operation, params string[] arguments)
    {
        var sdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var info = NewProcess(File.Exists(sdk) ? sdk : "dotnet");
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.RedirectStandardOutput = info.RedirectStandardError = true;
        Report(operation + "…");
        using var child = Process.Start(info) ?? throw new InvalidOperationException(operation + "启动失败。");
        await Task.WhenAll(Forward(child.StandardOutput), Forward(child.StandardError), child.WaitForExitAsync());
        if (child.ExitCode != 0)
        {
            throw new InvalidOperationException($"{operation}失败，退出码 {child.ExitCode}。");
        }

        Report(operation + "成功。");
    }

    private async Task Forward(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Report(line);
        }
    }

    private void Report(string text) => Output?.Invoke(text);

    public void Dispose()
    {
        foreach (var instance in Instances)
        {
            instance.Dispose();
        }
    }
}
