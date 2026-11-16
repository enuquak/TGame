using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TGame.ServerConsole;

internal sealed record LogEntry(long Number, string Raw, string Time, string Event, string Content)
{
    private const string FailureEventMarker = "FAILED";
    private const string ErrorEventMarker = "ERROR";

    public Brush EventColor => Event.Contains(FailureEventMarker, StringComparison.Ordinal) || Event.Contains(ErrorEventMarker, StringComparison.Ordinal)
        ? Brushes.LightSalmon : Brushes.PaleTurquoise;

    public bool Matches(string keyword) =>
        Time.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
        Event.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
        Content.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    public static LogEntry Parse(long number, string raw)
    {
        if (raw.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                if (root.TryGetProperty(ServerLogFields.Event, out var ev) && ev.ValueKind == JsonValueKind.String)
                {
                    var time = root.TryGetProperty(ServerLogFields.Time, out var stamp) && stamp.ValueKind == JsonValueKind.String &&
                               stamp.TryGetDateTimeOffset(out var instant)
                        ? instant.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) : "—";
                    var parts = new List<string>();
                    foreach (var field in root.EnumerateObject())
                    {
                        if (field.Name is ServerLogFields.Time or ServerLogFields.Event or ServerLogFields.Role or ServerLogFields.Pid ||
                            field.Value.ValueKind == JsonValueKind.Null)
                        {
                            continue;
                        }

                        var value = field.Value.ToString();
                        if (value.Length > 0)
                        {
                            parts.Add(field.Name == ServerLogFields.Detail ? value : field.Name + "=" + value);
                        }
                    }

                    return new(number, raw, time, ev.GetString()!, string.Join("   ", parts));
                }
            }
            catch (JsonException)
            {
                // Raw stderr may also start with a brace.
            }
        }

        return new(number, raw, "—", "输出", raw);
    }
}

public sealed class LogText : TextBlock
{
    private bool highlighted;
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string),
        typeof(LogText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty KeywordProperty = DependencyProperty.Register(nameof(Keyword), typeof(string),
        typeof(LogText), new PropertyMetadata("", Changed));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Keyword
    {
        get => (string)GetValue(KeywordProperty);
        set => SetValue(KeywordProperty, value);
    }

    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var cell = (LogText)owner;
        var value = cell.Value ?? "";
        var keyword = cell.Keyword ?? "";
        var match = keyword.Length == 0 ? -1 : value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (match < 0)
        {
            if (cell.highlighted)
            {
                cell.Inlines.Clear();
            }

            cell.highlighted = false;
            cell.Text = value;
            return;
        }

        cell.Inlines.Clear();
        cell.highlighted = true;
        var start = 0;
        while (match >= 0)
        {
            if (match > start)
            {
                cell.Inlines.Add(new Run(value[start..match]));
            }

            cell.Inlines.Add(new Run(value.Substring(match, keyword.Length)) { Background = Brushes.Khaki, Foreground = Brushes.Black });
            start = match + keyword.Length;
            match = value.IndexOf(keyword, start, StringComparison.OrdinalIgnoreCase);
        }

        if (start < value.Length)
        {
            cell.Inlines.Add(new Run(value[start..]));
        }
    }
}
