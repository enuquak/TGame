namespace TGame.Server.Storage;

internal sealed class RedisStorageSettings
{
    public string Endpoint { get; init; } = "";
    public string UserName { get; init; } = "";
    public string PasswordVariable { get; init; } = "";
    public string ProfileCachePrefix { get; init; } = "";

    public void Validate()
    {
        string[] values = [Endpoint, UserName, PasswordVariable, ProfileCachePrefix];
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("Incomplete Redis storage configuration.");
        }
    }
}
