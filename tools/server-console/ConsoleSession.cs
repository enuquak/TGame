using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace TGame.ServerConsole;

internal sealed class ConsoleSession : IDisposable
{
    internal const string SessionMarker = "console";

    private const string Project = "code/server/app/TGame.Server.csproj";
    private const string EngineProject = "code/server/engine/TGame.Server.Engine.csproj";
    private const string ProtocolProject = "code/shared/TGame.Shared.csproj";
    private const string MongoPasswordKey = "MONGO_TEST_PASSWORD";
    private const string RedisPasswordKey = "REDIS_TEST_PASSWORD";
    private const string SessionDirectoryFormat = "yyyyMMdd-HHmmss-fffffff";

    private readonly string root, sessions, logRoot;
    private string? sessionName;
    private bool busy;
    public ConsoleProject ProjectSettings
    {
        get;
    }

    public IReadOnlyList<ServerInstance> Instances
    {
        get; private set;
    }

    public event Action<string>? Output;
    public event Action? InstancesChanged;

    public ConsoleSession(string repoRoot)
    {
        root = Path.GetFullPath(repoRoot);
        ProjectSettings = ConsoleProject.Load(Path.Combine(root, "tools", "server-console", "project.json"));
        sessions = Path.Combine(root, "logs", "tools", "server-console");
        logRoot = Path.Combine(root, "logs", "server");
        var current = Path.Combine(sessions, "current");
        if (!File.Exists(current))
        {
            var configured = LoadSelectedConfiguration().Group;
            Instances = CreateInstances(configured);
            return;
        }

        var saved = File.ReadAllText(current).Trim();
        bool validDirectoryName = saved.Length > 0 && saved.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
        if (!validDirectoryName || !File.Exists(Path.Combine(sessions, saved, SessionMarker)))
        {
            throw new InvalidDataException("控制台当前会话记录无效。");
        }

        if (!File.Exists(Path.Combine(sessions, saved, ServerRunFiles.DynamicDiscoveryMarker)))
        {
            throw new InvalidDataException($"当前运行组不支持动态身份。请先用旧版控制台停止整组，再删除会话指针 {current} 后重新打开控制台。");
        }

        var group = ServerGroup.Load(Path.Combine(sessions, saved, ServerGroup.FileName));
        Instances = CreateInstances(group);
        Open(saved);
    }

    private static ServerInstance[] CreateInstances(ServerGroup group) =>
        group.Processes.Select(process => new ServerInstance(process.Role, process.ChannelId)).ToArray();

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
            Launch(missing, sessionName!);
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

    public Task ExportAsync() => Command(async () =>
    {
        await Dotnet("导出引擎 Protobuf", "msbuild", EngineProject, "-restore", "-target:Protobuf_Compile", "-nologo", "-verbosity:minimal");
        await Dotnet("导出业务 Protobuf", "msbuild", ProtocolProject, "-restore", "-target:Protobuf_Compile", "-nologo", "-verbosity:minimal");
    });

    private Task Compile() => Dotnet("编译服务器", "build", Project, "--nologo");

    // 界面命令、计时器和异步续体由同一条 UI 线程调度。
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

    private string Snapshot(string name) => Path.Combine(root, "build", "server-console", "runs", name);

    private async Task StartNewRun()
    {
        var configuration = LoadSelectedConfiguration();
        var group = configuration.Group;
        var configuredNames = group.Processes.Select(process => process.Name);
        bool changed = !Instances.Select(instance => instance.SlotName).SequenceEqual(configuredNames);
        var nextInstances = Instances;
        if (changed)
        {
            nextInstances = CreateInstances(group);
        }

        var name = DateTime.UtcNow.ToString(SessionDirectoryFormat, CultureInfo.InvariantCulture);
        var snapshot = Snapshot(name);
        var runDir = Path.Combine(sessions, name);
        // 在停止健康进程前准备完整的启动信息和构建快照。
        await Task.Run(() =>
        {
            var source = Path.Combine(root, "build", "bin", "TGame.Server", "debug");
            var apphosts = nextInstances.Select(instance => Path.Combine(snapshot, instance.ExecutableName));
            PrepareSnapshot(source, snapshot, configuration, runDir, root, apphosts);
        });
        var runtimeConfig = Path.Combine(snapshot, "config", ServerRuntime.FileName);
        await PrepareRunDirectory(runtimeConfig);
        File.WriteAllText(Path.Combine(runDir, SessionMarker), "");
        File.WriteAllText(Path.Combine(runDir, ServerRunFiles.DynamicDiscoveryMarker), "");
        File.Copy(runtimeConfig, Path.Combine(runDir, ServerConfiguration.FileName));
        var starts = nextInstances.Select(i => StartInfo(i, name)).ToArray();
        await Task.WhenAll(Instances.Select(i => i.StopAsync(Report)));
        if (changed)
        {
            foreach (var instance in Instances)
            {
                instance.Dispose();
            }

            Instances = nextInstances;
        }

        var previous = sessionName;
        var current = Path.Combine(sessions, "current");
        File.WriteAllText(current + ".tmp", name);
        File.Move(current + ".tmp", current, true);
        Open(name);
        if (changed)
        {
            InstancesChanged?.Invoke();
        }

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

    internal static void PrepareSnapshot(string source, string snapshot, ServerConfiguration configuration,
        string runDirectory, string workingDirectory, IEnumerable<string> apphosts)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(snapshot, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }

        // 启动数量、连接关系和运行参数使用同一次加载的完整配置，补起时不再依赖源文件。
        var configDirectory = Directory.CreateDirectory(Path.Combine(snapshot, "config")).FullName;
        configuration.SaveSnapshot(Path.Combine(configDirectory, ServerConfiguration.FileName),
            Path.GetRelativePath(workingDirectory, runDirectory));
        foreach (var apphost in apphosts)
        {
            File.Copy(Path.Combine(snapshot, "TGame.Server.exe"), apphost);
        }
    }

    private ServerConfiguration LoadSelectedConfiguration()
    {
        var selected = Environment.GetEnvironmentVariable(EngineEnvironment.Config);
        if (string.IsNullOrWhiteSpace(selected))
        {
            selected = ServerConfiguration.ConfigurationPath;
        }

        return new ServerConfiguration(Path.GetFullPath(selected, root));
    }

    private void Open(string name)
    {
        sessionName = name;
        foreach (var instance in Instances)
        {
            instance.Open(Path.Combine(sessions, name), logRoot, Snapshot(name));
        }
    }

    private void Launch(ServerInstance[] missing, string name)
    {
        var starts = missing.Select(i => StartInfo(i, name)).ToArray();
        for (int i = 0; i < missing.Length; i++)
        {
            Launch(missing[i], starts[i]);
        }
    }

    private void Launch(ServerInstance instance, ProcessStartInfo start)
    {
        instance.Launch(start);
        Report($"已启动 {instance.Role} 进程 · PID {instance.Pid}，实例身份由服务器注册后显示");
    }

    private ProcessStartInfo StartInfo(ServerInstance instance, string name)
    {
        if (!File.Exists(Path.Combine(Snapshot(name), "config", ServerRuntime.FileName)))
        {
            throw new InvalidOperationException("当前运行组使用旧版启动参数，请使用“编译并重启”升级后再补起进程；仍可正常关闭原有进程。");
        }

        var info = NewProcess(Path.Combine(Snapshot(name), instance.ExecutableName));
        var runDir = Path.Combine(sessions, name);
        info.ArgumentList.Add(instance.Role);
        var config = Path.Combine(runDir, ServerConfiguration.FileName);
        if (!File.Exists(config))
        {
            throw new InvalidOperationException("当前运行缺少环境配置快照，请选择环境后编译并重启。");
        }

        info.Environment[EngineEnvironment.Config] = config;
        info.Environment[EngineEnvironment.LogRoot] = logRoot;
        return info;
    }

    private ProcessStartInfo NewProcess(string exe)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true };
        foreach (var key in new[] { ServerEnvironment.DBMgrMongoPassword, ServerEnvironment.MongoPassword,
            ServerEnvironment.RedisPassword, MongoPasswordKey, RedisPasswordKey })
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

        await RunProcess(operation, info);
    }

    private async Task PrepareRunDirectory(string configuration)
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        var info = NewProcess(File.Exists(installed) ? installed : "pwsh.exe");
        string[] arguments = ["-NoProfile", "-File", Path.Combine(root, "tools", "scripts", "powershell", "New-ServerRunDirectory.ps1"),
            "-Configuration", configuration];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        await RunProcess("创建运行目录", info);
    }

    private async Task RunProcess(string operation, ProcessStartInfo info)
    {
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
