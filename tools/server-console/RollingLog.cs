using System.IO;
using System.Text;
using TGame.Server.Engine;

namespace TGame.ServerConsole;

// 按分片时间和同分钟序号读取；进程归属由日志行中的身份与时间判断。
internal sealed class RollingLog(string directory, long startedUtcTicks)
{
    private const int BufferSizeBytes = 16 * 1024;
    private const int ReadBudgetBytes = 256 * 1024;

    private readonly byte[] buffer = new byte[BufferSizeBytes];
    private readonly Dictionary<(DateTime Time, int Part), Cursor> cursors = [];

    private sealed class Cursor
    {
        internal long Offset;
        internal MemoryStream? Partial;
    }

    internal bool ReadAvailable(Action<string> accept)
    {
        if (!Directory.Exists(directory))
        {
            return true;
        }

        var started = new DateTime(startedUtcTicks, DateTimeKind.Utc).ToLocalTime();
        var firstHour = started.Date.AddHours(started.Hour);
        var files = new List<(string Path, (DateTime Time, int Part) Key)>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.log"))
        {
            if (ServerLogFiles.TryParse(path, out var time, out int part) && time >= firstHour)
            {
                files.Add((path, (time, part)));
            }
        }

        var ordered = files.OrderBy(file => file.Key.Time).ThenBy(file => file.Key.Part);
        // Limit work on each UI tick when opening a session with a large backlog.
        int budget = ReadBudgetBytes;
        foreach (var file in ordered)
        {
            using var stream = Open(file.Path);
            if (stream == null)
            {
                continue;
            }

            if (!cursors.TryGetValue(file.Key, out var cursor))
            {
                cursor = new Cursor();
                cursors.Add(file.Key, cursor);
            }

            if (stream.Length < cursor.Offset)
            {
                cursor.Offset = 0;
                cursor.Partial?.Dispose();
                cursor.Partial = null;
            }

            stream.Position = cursor.Offset;
            int count;
            while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, budget))) > 0)
            {
                cursor.Offset += count;
                budget -= count;
                int start = 0;
                for (int end = Array.IndexOf(buffer, (byte)'\n', 0, count); end >= 0;
                     end = Array.IndexOf(buffer, (byte)'\n', start, count - start))
                {
                    string line;
                    if (cursor.Partial is { } partial)
                    {
                        partial.Write(buffer, start, end - start);
                        line = Encoding.UTF8.GetString(partial.GetBuffer(), 0, (int)partial.Length);
                        partial.Dispose();
                        cursor.Partial = null;
                    }
                    else
                    {
                        line = Encoding.UTF8.GetString(buffer, start, end - start);
                    }

                    line = line.TrimEnd('\r');
                    if (line.Length != 0)
                    {
                        accept(line);
                    }

                    start = end + 1;
                }

                if (start < count)
                {
                    cursor.Partial ??= new MemoryStream();
                    cursor.Partial.Write(buffer, start, count - start);
                }

                if (budget == 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static FileStream? Open(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
