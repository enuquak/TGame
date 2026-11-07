using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TGame.ServerConsole;

internal sealed class ServerInstance(string role) : IDisposable
{
    private sealed record Identity(int Pid, long StartedUtcTicks);
    private readonly List<string> lines = [];
    private Process? process;
    private string runDir = "";
    private string logDir = "";
    private string runId = "";
    private long startedUtcTicks;
    private RollingLog? log;
    private bool initializing, initialized, connected, failed;

    public string Role { get; } = role;
    public int? Pid { get; private set; }
    public string Status { get; private set; } = "未启动";
    public IReadOnlyList<string> Lines => lines;
    public long LineCount { get; private set; }
    internal bool Alive => process is { HasExited: false };
    internal string ExecutableName => Role + "Server.exe";
    private string IdentityPath => Path.Combine(runDir, Role + ".json");
    private string LaunchMarker => Path.Combine(runDir, "starting-" + Role);
    private string StopMarker => Path.Combine(runDir, "stop-" + Role);

    internal void Open(string directory, string logRoot, string snapshot)
    {
        Reset();
        runDir = directory;
        runId = Path.GetFileName(directory);
        logDir = Path.Combine(logRoot, Role + "Server");
        var exe = Path.Combine(snapshot, ExecutableName);
        var saved = File.Exists(IdentityPath)
            ? JsonSerializer.Deserialize<Identity>(File.ReadAllText(IdentityPath)) ?? throw new InvalidDataException(IdentityPath)
            : null;
        Pid = saved?.Pid;
        startedUtcTicks = saved?.StartedUtcTicks ?? 0;
        bool pending = File.Exists(LaunchMarker);
        if (pending || saved != null) RestoreProcess(exe, saved, pending);
        if (pending) File.Delete(LaunchMarker);
        if (Pid != null) log = new RollingLog(logDir, $"{runId}-{Pid}-");
        Refresh();
    }

    private void RestoreProcess(string exe, Identity? saved, bool pending)
    {
        // The unique role apphost also recovers a child if the UI closed between Start and saving its PID.
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            try
            {
                if (!pending && candidate.Id != saved!.Pid) continue;
                long ticks = candidate.StartTime.ToUniversalTime().Ticks;
                if (!pending && ticks != saved!.StartedUtcTicks) continue;
                if (!string.Equals(candidate.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (process != null) throw new InvalidDataException($"{Role} 在同一会话中有多个实例。");
                process = candidate;
                Pid = candidate.Id;
                startedUtcTicks = ticks;
                var actual = new Identity(candidate.Id, ticks);
                if (actual != saved) Save(actual);
            }
            catch (InvalidOperationException) when (candidate.HasExited) { }
            catch (Win32Exception) when (candidate.HasExited) { }
            finally { if (!ReferenceEquals(candidate, process)) candidate.Dispose(); }
        }
    }

    internal void Launch(ProcessStartInfo startInfo)
    {
        File.Delete(StopMarker);
        File.WriteAllText(LaunchMarker, "");
        Reset();
        process = Process.Start(startInfo) ?? throw new InvalidOperationException($"{Role} 启动失败。");
        Pid = process.Id;
        log = new RollingLog(logDir, $"{runId}-{Pid}-");
        startedUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        Save(new Identity(process.Id, startedUtcTicks));
        File.Delete(LaunchMarker);
        Status = "启动中";
    }

    private void Save(Identity identity)
    {
        File.WriteAllText(IdentityPath + ".tmp", JsonSerializer.Serialize(identity));
        File.Move(IdentityPath + ".tmp", IdentityPath, true);
    }

    internal void Refresh()
    {
        if (Pid == null) return;
        bool caughtUp;
        try { caughtUp = log?.ReadAvailable(AddLine) ?? true; }
        catch (IOException e) when (e is DirectoryNotFoundException || (e.HResult & 0xffff) is 32 or 33)
        {
            // Retry a shard that disappeared or was temporarily locked while the server rolled its log.
            caughtUp = false;
        }
        if (lines.Count > 1000) lines.RemoveRange(0, lines.Count - 1000);
        bool stopping = File.Exists(StopMarker);
        if (failed) Status = "异常退出";
        else if (!Alive) Status = stopping ? "已停止" : "异常退出";
        else if (stopping) Status = "停止中";
        else if (!caughtUp) Status = "初始化中";
        else if (initialized) Status = connected ? "就绪" : "连接不全";
        else Status = initializing ? "初始化中" : "启动中";
    }

    private void AddLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            if (row.GetProperty("runId").GetString() != runId || row.GetProperty("role").GetString() != Role ||
                row.GetProperty("pid").GetInt32() != Pid) return;
            if (row.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.String &&
                time.TryGetDateTimeOffset(out var stamp) && stamp.UtcTicks < startedUtcTicks) return;
            ApplyEvent(row.GetProperty("ev").GetString());
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
            case "LISTEN":
            case "SINGLE_POINT_INIT": initializing = true; break;
            case "SINGLE_POINT_READY": initialized = true; break;
            case "LINKS_READY": connected = true; break;
            case "LINKS_LOST": connected = false; break;
            case "PROCESS_FAILED": failed = true; break;
        }
    }

    internal async Task StopAsync(Action<string> report)
    {
        if (process is not { HasExited: false } child) return;
        File.WriteAllText(StopMarker, "");
        Status = "停止中";
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException)
        {
            if (!child.HasExited)
            {
                report($"{Role} 超过 10 秒未退出，终止进程 {child.Id}。");
                try { child.Kill(); }
                catch (InvalidOperationException) when (child.HasExited) { }
            }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Refresh();
    }

    private void Reset()
    {
        Dispose();
        process = null;
        Pid = null;
        log = null;
        lines.Clear();
        LineCount = 0;
        initializing = initialized = connected = failed = false;
        Status = "未启动";
    }

    public void Dispose() => process?.Dispose();
}
