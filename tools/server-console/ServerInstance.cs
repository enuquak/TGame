using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TGame.ServerConsole;

internal static class ServerStatus
{
    public const string NotStarted = "未启动";
    public const string Starting = "启动中";
    public const string Initializing = "初始化中";
    public const string Ready = "就绪";
    public const string Disconnected = "连接不全";
    public const string Stopping = "停止中";
    public const string Stopped = "已停止";
    public const string Failed = "异常退出";
}

internal sealed class ServerInstance(string role, int channelId = 1) : IDisposable
{
    internal const int MaxRetainedLines = 1000;
    private const int StopTimeoutSeconds = 10;
    private const int KillTimeoutSeconds = 5;
    private const int HResultCodeMask = 0xffff;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;

    private sealed record Identity(int Pid, long StartedUtcTicks);
    private readonly List<string> lines = [];
    private Process? process;
    private string runDir = "";
    private string logDir = "";
    private string logRoot = "";
    private bool identityResolved;
    private long startedUtcTicks;
    private RollingLog? log;
    private bool initializing, initialized, connected, failed;

    public string Role { get; } = role;
    public string Name { get; private set; } = ServerProcess.FormatName(role, channelId);
    internal string SlotName { get; } = ServerProcess.FormatName(role, channelId);
    public int? Pid
    {
        get; private set;
    }

    public string Status { get; private set; } = ServerStatus.NotStarted;
    public IReadOnlyList<string> Lines => lines;
    public long LineCount
    {
        get; private set;
    }

    internal bool Alive => process is { HasExited: false };
    internal string ExecutableName => SlotName + "Server.exe";
    private string IdentityPath => Path.Combine(runDir, SlotName + ".json");
    private string LaunchMarker => Path.Combine(runDir, "starting-" + SlotName);

    internal void Open(string directory, string logRoot, string snapshot)
    {
        Reset();
        runDir = directory;
        this.logRoot = logRoot;
        logDir = Path.Combine(logRoot, Name);
        var exe = Path.Combine(snapshot, ExecutableName);
        var saved = File.Exists(IdentityPath)
            ? JsonSerializer.Deserialize<Identity>(File.ReadAllText(IdentityPath)) ?? throw new InvalidDataException(IdentityPath)
            : null;
        Pid = saved?.Pid;
        startedUtcTicks = saved?.StartedUtcTicks ?? 0;
        bool pending = File.Exists(LaunchMarker);
        if (pending || saved != null)
        {
            RestoreProcess(exe, saved, pending);
        }

        if (pending)
        {
            File.Delete(LaunchMarker);
        }

        if (Pid != null)
        {
            log = new RollingLog(logDir, startedUtcTicks);
        }

        Refresh();
    }

    private void RestoreProcess(string exe, Identity? saved, bool pending)
    {
        // The unique instance apphost also recovers a child if the UI closed between Start and saving its PID.
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            try
            {
                if (!pending && candidate.Id != saved!.Pid)
                {
                    continue;
                }

                long ticks = candidate.StartTime.ToUniversalTime().Ticks;
                if (!pending && ticks != saved!.StartedUtcTicks)
                {
                    continue;
                }

                if (!string.Equals(candidate.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (process != null)
                {
                    throw new InvalidDataException($"{Name} 在同一会话中有多个实例。");
                }

                process = candidate;
                Pid = candidate.Id;
                startedUtcTicks = ticks;
                var actual = new Identity(candidate.Id, ticks);
                if (actual != saved)
                {
                    Save(actual);
                }
            }
            catch (InvalidOperationException) when (candidate.HasExited)
            {
            }
            catch (Win32Exception) when (candidate.HasExited)
            {
            }
            finally
            {
                if (!ReferenceEquals(candidate, process))
                {
                    candidate.Dispose();
                }
            }
        }
    }

    internal void Launch(ProcessStartInfo startInfo)
    {
        File.WriteAllText(LaunchMarker, "");
        Reset();
        process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{Name} 启动失败。");
        Pid = process.Id;
        startedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        log = new RollingLog(logDir, startedUtcTicks);
        Save(new Identity(process.Id, startedUtcTicks));
        File.Delete(LaunchMarker);
        Status = ServerStatus.Starting;
    }

    private void Save(Identity identity)
    {
        File.WriteAllText(IdentityPath + ".tmp", JsonSerializer.Serialize(identity));
        File.Move(IdentityPath + ".tmp", IdentityPath, true);
    }

    internal void Refresh()
    {
        if (Pid == null)
        {
            return;
        }

        if (!identityResolved)
        {
            var identityPath = Path.Combine(runDir, ServerRunFiles.IdentityPrefix + ServerRunFiles.ProcessKey(Pid.Value, startedUtcTicks));
            if (File.Exists(identityPath))
            {
                var assigned = ServerProcess.Parse(File.ReadAllText(identityPath));
                if (assigned.Role != Role)
                {
                    throw new InvalidDataException("Claimed process type does not match its launch type.");
                }

                Name = assigned.Name;
                logDir = Path.Combine(logRoot, Name);
                log = new RollingLog(logDir, startedUtcTicks);
                identityResolved = true;
            }
        }

        bool caughtUp;
        try
        {
            caughtUp = log?.ReadAvailable(AddLine) ?? true;
        }
        catch (IOException e) when (e is DirectoryNotFoundException || (e.HResult & HResultCodeMask) is SharingViolation or LockViolation)
        {
            // Retry a shard that disappeared or was temporarily locked while the server rolled its log.
            caughtUp = false;
        }

        if (lines.Count > MaxRetainedLines)
        {
            lines.RemoveRange(0, lines.Count - MaxRetainedLines);
        }

        var stopMarker = Path.Combine(runDir, ServerRunFiles.StopProcessPrefix + ServerRunFiles.ProcessKey(Pid.Value, startedUtcTicks));
        bool stopping = File.Exists(stopMarker);
        if (failed)
        {
            Status = ServerStatus.Failed;
            return;
        }

        if (!Alive)
        {
            Status = stopping ? ServerStatus.Stopped : ServerStatus.Failed;
            return;
        }

        if (stopping)
        {
            Status = ServerStatus.Stopping;
            return;
        }

        if (!caughtUp)
        {
            Status = ServerStatus.Initializing;
            return;
        }

        if (initialized)
        {
            Status = connected ? ServerStatus.Ready : ServerStatus.Disconnected;
            return;
        }

        Status = initializing ? ServerStatus.Initializing : ServerStatus.Starting;
    }

    private void AddLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            if (row.GetProperty(ServerLogFields.Role).GetString() != Role ||
                row.GetProperty(ServerLogFields.Pid).GetInt32() != Pid)
            {
                return;
            }

            if (row.TryGetProperty(ServerLogFields.Instance, out var instance) && instance.GetString() != Name)
            {
                return;
            }

            if (row.TryGetProperty(ServerLogFields.Time, out var time) && time.ValueKind == JsonValueKind.String &&
                time.TryGetDateTimeOffset(out var stamp) && stamp.UtcTicks < startedUtcTicks)
            {
                return;
            }

            ApplyEvent(row.GetProperty(ServerLogFields.Event).GetString());
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // A damaged record is still visible for diagnosis, but cannot change process state.
        }

        lines.Add(line);
        LineCount++;
    }

    private void ApplyEvent(string? name)
    {
        switch (name)
        {
            case ServerLogEvents.Listen:
                initializing = true;
                break;
            case ServerLogEvents.ProcessReady:
                initialized = true;
                break;
            case ServerLogEvents.LinksReady:
                connected = true;
                break;
            case ServerLogEvents.LinksLost:
                connected = false;
                break;
            case ServerLogEvents.ProcessFailed:
                failed = true;
                break;
        }
    }

    internal async Task StopAsync(Action<string> report)
    {
        if (process is not { HasExited: false } child)
        {
            return;
        }

        var stopMarker = Path.Combine(runDir, ServerRunFiles.StopProcessPrefix + ServerRunFiles.ProcessKey(child.Id, startedUtcTicks));
        File.WriteAllText(stopMarker, "");
        Status = ServerStatus.Stopping;
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(StopTimeoutSeconds));
        }
        catch (TimeoutException)
        {
            if (!child.HasExited)
            {
                report($"{Name} 超过 {StopTimeoutSeconds} 秒未退出，终止进程 {child.Id}。");
                try
                {
                    child.Kill();
                }
                catch (InvalidOperationException) when (child.HasExited)
                {
                }
            }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(KillTimeoutSeconds));
        }
        Refresh();
    }

    private void Reset()
    {
        Dispose();
        process = null;
        Pid = null;
        Name = SlotName;
        identityResolved = false;
        logDir = Path.Combine(logRoot, Name);
        log = null;
        lines.Clear();
        LineCount = 0;
        initializing = initialized = connected = failed = false;
        Status = ServerStatus.NotStarted;
    }

    public void Dispose() => process?.Dispose();
}
