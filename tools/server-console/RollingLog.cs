using System.IO;
using System.Text;

namespace TGame.ServerConsole;

// Serilog rolls by yyyyMMddHH, then by numeric size suffix within each hour.
internal sealed class RollingLog(string directory, string prefix)
{
    private const int BufferSizeBytes = 16 * 1024;
    private const int ReadBudgetBytes = 256 * 1024;

    private readonly byte[] buffer = new byte[BufferSizeBytes];
    private readonly MemoryStream partial = new();
    private (string Hour, int Part)? current;
    private long offset;

    internal bool ReadAvailable(Action<string> accept)
    {
        if (!Directory.Exists(directory))
        {
            return true;
        }

        var files = Directory.EnumerateFiles(directory, prefix + "*.log")
            .Select(path => (Path: path, Key: RollKey(path)))
            .OrderBy(file => file.Key.Hour, StringComparer.Ordinal).ThenBy(file => file.Key.Part);
        // Limit work on each UI tick when opening a session with a large backlog.
        int budget = ReadBudgetBytes;
        foreach (var file in files)
        {
            if (current is { } previous &&
                (string.CompareOrdinal(file.Key.Hour, previous.Hour) < 0 ||
                 file.Key.Hour == previous.Hour && file.Key.Part < previous.Part))
            {
                continue;
            }

            using var stream = Open(file.Path);
            if (stream == null)
            {
                continue;
            }

            if (current != file.Key || stream.Length < offset)
            {
                current = file.Key;
                offset = 0;
                partial.SetLength(0);
            }

            stream.Position = offset;
            int count;
            while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, budget))) > 0)
            {
                offset += count;
                budget -= count;
                int start = 0;
                for (int end = Array.IndexOf(buffer, (byte)'\n', 0, count); end >= 0;
                     end = Array.IndexOf(buffer, (byte)'\n', start, count - start))
                {
                    partial.Write(buffer, start, end - start);
                    string line = Encoding.UTF8.GetString(partial.GetBuffer(), 0, (int)partial.Length).TrimEnd('\r');
                    partial.SetLength(0);
                    if (line.Length != 0)
                    {
                        accept(line);
                    }

                    start = end + 1;
                }

                partial.Write(buffer, start, count - start);
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

    private (string Hour, int Part) RollKey(string path)
    {
        var suffix = Path.GetFileNameWithoutExtension(path)[prefix.Length..];
        int split = suffix.IndexOf('_');
        return split >= 0 && int.TryParse(suffix.AsSpan(split + 1), out int part) ? (suffix[..split], part) : (suffix, 0);
    }
}
