using System.Globalization;
using System.Text;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace TGame.Server.Engine;

internal sealed class ServerFileSink(string directory, long fileSizeLimit) : ILogEventSink, IDisposable
{
    private const int HResultCodeMask = 0xffff;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private readonly object sync = new();
    private readonly MessageTemplateTextFormatter formatter = new("{Message:lj}{NewLine}", CultureInfo.InvariantCulture);
    private StreamWriter? writer;
    private DateTime fileTime;
    private int filePart;
    private bool disposed;

    public void Emit(LogEvent logEvent)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var now = logEvent.Timestamp.LocalDateTime;
            var hour = now.Date.AddHours(now.Hour);
            var minute = hour.AddMinutes(now.Minute);
            if (writer is null || hour > fileTime.Date.AddHours(fileTime.Hour))
            {
                CloseFile();
                writer = OpenLatest(hour, minute);
            }

            while (writer.BaseStream.Length >= fileSizeLimit)
            {
                // 时钟回退时仍沿当前分片向后写，保持读取游标的文件顺序。
                if (minute < fileTime)
                {
                    minute = fileTime;
                }

                int part = minute == fileTime ? checked(filePart + 1) : 0;
                CloseFile();
                writer = OpenFile(minute, part, minute);
            }

            formatter.Format(logEvent, writer);
            writer.Flush();
        }
    }

    private StreamWriter OpenLatest(DateTime hour, DateTime minute)
    {
        Directory.CreateDirectory(directory);
        var latest = hour;
        int part = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.log"))
        {
            if (!ServerLogFiles.TryParse(path, out var timestamp, out int candidatePart) ||
                timestamp < hour || timestamp >= hour.AddHours(1))
            {
                continue;
            }

            if (timestamp > latest || timestamp == latest && candidatePart > part)
            {
                latest = timestamp;
                part = candidatePart;
            }
        }

        return OpenFile(latest, part, minute);
    }

    private StreamWriter OpenFile(DateTime timestamp, int part, DateTime minute)
    {
        while (true)
        {
            var path = Path.Combine(directory, ServerLogFiles.FileName(timestamp, part));
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                try
                {
                    bool incomplete = false;
                    if (stream.Length > 0)
                    {
                        stream.Seek(-1, SeekOrigin.End);
                        incomplete = stream.ReadByte() != '\n';
                    }

                    stream.Seek(0, SeekOrigin.End);
                    var opened = new StreamWriter(stream, new UTF8Encoding(false));
                    if (incomplete)
                    {
                        // 保留上次异常退出的半行，新进程的事件从独立行开始。
                        opened.WriteLine();
                        opened.Flush();
                    }

                    fileTime = timestamp;
                    filePart = part;
                    return opened;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException error) when ((error.HResult & HResultCodeMask) is SharingViolation or LockViolation)
            {
                if (timestamp < minute)
                {
                    timestamp = minute;
                    part = 0;
                }
                else
                {
                    part = checked(part + 1);
                }
            }
        }
    }

    private void CloseFile()
    {
        writer?.Dispose();
        writer = null;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CloseFile();
        }
    }
}
