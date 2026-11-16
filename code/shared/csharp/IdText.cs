#nullable enable

using System.Globalization;

namespace TGame;

public static class IdText
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] == '0')
        {
            return false;
        }

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0;
    }
}
