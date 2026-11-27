using System.Globalization;

namespace TGame.Server.Engine;

public static class ServerLogFiles
{
    private const string TimestampFormat = "yyyyMMddHHmm";

    internal static string FileName(DateTime timestamp, int part)
    {
        var name = timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        if (part > 0)
        {
            name += "_" + part.ToString("000", CultureInfo.InvariantCulture);
        }

        return name + ".log";
    }

    public static bool TryParse(string path, out DateTime timestamp, out int part)
    {
        timestamp = default;
        part = 0;
        if (Path.GetExtension(path) != ".log")
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        int split = name.IndexOf('_');
        var time = split < 0 ? name : name[..split];
        if (!DateTime.TryParseExact(time, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp))
        {
            return false;
        }

        if (split < 0)
        {
            return true;
        }

        var suffix = name[(split + 1)..];
        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out part) &&
            part > 0 && suffix == part.ToString("000", CultureInfo.InvariantCulture);
    }
}
