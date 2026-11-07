using System.Security.Cryptography;
using System.Text;

namespace TGame.Server.Logic;

[SinglePointFor(ProcessType.GM)]
internal sealed class GMServerSinglePoint : ServerSinglePoint
{
    public string? Actor { get; private set; }
    public byte[]? TokenHash { get; private set; }

    protected override Task InitializeRoleAsync(CancellationToken stop)
    {
        var secret = Environment.GetEnvironmentVariable("TGAME_GM_TOKEN");
        Actor = Environment.GetEnvironmentVariable("TGAME_GM_OPERATOR");

        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32 || string.IsNullOrWhiteSpace(Actor) || Actor.Length > 64)
            throw new InvalidOperationException("GM requires TGAME_GM_TOKEN (at least 32 characters) and TGAME_GM_OPERATOR (1–64 characters).");

        TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Task.CompletedTask;
    }
}
