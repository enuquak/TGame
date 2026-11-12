using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TGame.Server.Storage;

internal sealed class MongoStorageSettings
{
    public required string Endpoint
    {
        get;
        init;
    }

    public required string DatabaseName
    {
        get;
        init;
    }

    public required string AuthenticationDatabase
    {
        get;
        init;
    }

    public required string UserName
    {
        get;
        init;
    }

    public required int ServerSelectionTimeoutSeconds
    {
        get;
        init;
    }

    public static MongoStorageSettings Load(string path)
    {
        var yaml = File.ReadAllText(path);
        var builder = new DeserializerBuilder();
        builder.WithNamingConvention(CamelCaseNamingConvention.Instance);

        var deserializer = builder.Build();
        var settings = deserializer.Deserialize<MongoStorageSettings>(yaml);
        if (settings is null)
        {
            throw new InvalidDataException($"MongoDB configuration is empty: {path}");
        }

        settings.Validate();
        return settings;
    }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(AuthenticationDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(UserName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ServerSelectionTimeoutSeconds);
    }
}
