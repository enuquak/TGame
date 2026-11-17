using System.Text.Json;

namespace TGame.Logging;

public static class LogEventJson
{
    public static string Serialize(ServerLogLevel level, string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null, string? instance = null)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [ServerLogFields.Time] = DateTimeOffset.UtcNow,
            [ServerLogFields.Level] = level.ToString().ToUpperInvariant(),
            [ServerLogFields.Event] = ev,
            [ServerLogFields.Role] = role,
            [ServerLogFields.Instance] = instance ?? role,
            [ServerLogFields.Pid] = Environment.ProcessId,
            [ServerLogFields.Peer] = peer,
            [ServerLogFields.Id] = id,
            [ServerLogFields.Entity] = entity,
            [ServerLogFields.Link] = link,
            [ServerLogFields.Detail] = detail
        });
    }
}
