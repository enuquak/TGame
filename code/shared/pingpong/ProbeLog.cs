using System.Text.Json;

namespace TGame.PingPong;

public static class ProbeLog
{
    public static string Serialize(string ev, string role, string? peer = null, string? id = null,
        string? entity = null, string? link = null, string? detail = null, string? runId = null)
    {
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            [ServerLogFields.Time] = DateTimeOffset.UtcNow,
            [ServerLogFields.Event] = ev,
            [ServerLogFields.Role] = role,
            [ServerLogFields.Pid] = Environment.ProcessId,
            [ServerLogFields.RunId] = runId,
            [ServerLogFields.Peer] = peer,
            [ServerLogFields.Id] = id,
            [ServerLogFields.Entity] = entity,
            [ServerLogFields.Link] = link,
            [ServerLogFields.Detail] = detail
        });
    }
}
