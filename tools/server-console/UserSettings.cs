using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TGame.ServerConsole;

internal sealed record LogPreference(string Keyword, bool MatchesOnly, bool Following);

internal sealed class UserSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };
    private string path = "";
    public string? SelectedRole
    {
        get; set;
    }

    public double Width
    {
        get; set;
    }

    public double Height
    {
        get; set;
    }

    public bool Maximized
    {
        get; set;
    }

    public Dictionary<string, LogPreference> Logs { get; set; } = [];
    [JsonIgnore]
    public string? Warning
    {
        get; private set;
    }

    public static UserSettings Load(string path)
    {
        UserSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), Json) ?? throw new JsonException();
            if (settings.Logs is null || settings.Logs.Values.Any(pref => pref?.Keyword is null))
            {
                throw new JsonException();
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            settings = new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            settings = new()
            {
                Warning = $"无法读取个人设置，已使用默认显示。{path} · {ex.Message}"
            };
        }

        settings.path = path;
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        if (Warning != null && File.Exists(path))
        {
            File.Replace(temp, path, path + ".bak");
        }
        else
        {
            File.Move(temp, path, true);
        }

        Warning = null;
    }
}
