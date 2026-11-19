using System;
using System.IO;

namespace TGame;

public sealed class MongoStorageSettings
{
    public const string SectionName = "mongo";
    public MongoPasswordVariables PasswordVariables { get; init; } = new();
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
        var settings = new ServerConfiguration(path).Mongo
            ?? throw new InvalidDataException("Missing MongoDB storage configuration.");
        settings.Validate();
        return settings;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(DatabaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(AuthenticationDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(UserName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ServerSelectionTimeoutSeconds);
    }
}

public sealed class MongoPasswordVariables
{
    public string Profile { get; init; } = "";
    public string Dbmgr { get; init; } = "";
}
