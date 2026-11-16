using System.Security.Cryptography;
using System.Text;
using TGame.Server.Engine;

namespace TGame.Server.Logic;

[SinglePointFor(ProcessType.GM)]
internal sealed class GMServerSinglePoint : ServerSinglePoint
{
    private const int MinimumTokenLength = 32;
    private const int MaximumOperatorLength = 64;

    public string? Actor
    {
        get;
        private set;
    }

    public byte[]? TokenHash
    {
        get;
        private set;
    }

    protected override Task InitializeRoleAsync(CancellationToken stop)
    {
        var secret = Environment.GetEnvironmentVariable(ServerEnvironment.GMToken);
        Actor = Environment.GetEnvironmentVariable(ServerEnvironment.GMOperator);

        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinimumTokenLength ||
            string.IsNullOrWhiteSpace(Actor) || Actor.Length > MaximumOperatorLength)
        {
            throw new InvalidOperationException($"GM requires {ServerEnvironment.GMToken} (at least {MinimumTokenLength} characters) and {ServerEnvironment.GMOperator} (1–{MaximumOperatorLength} characters).");
        }

        TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Task.CompletedTask;
    }
}
