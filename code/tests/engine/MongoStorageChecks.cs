using System.Net;
using System.Net.Sockets;
using TGame;
using TGame.Server.Storage;
using YamlDotNet.Serialization;

internal static class MongoStorageChecks
{
    private const int SelectionTimeoutSeconds = 1;
    private const int TestTimeoutSeconds = 5;
    private const string DatabaseName = "configured_database";
    private const string AuthenticationDatabase = "configured_auth";
    private const string UserName = "configured_user";
    private const string TestPassword = "storage-check-only";

    public static async Task RunAsync()
    {
        var config = Path.GetTempFileName();
        var document = ConfigurationFixture.CreateDocument();
        void WriteSettings(string yaml)
        {
            var settings = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);
            document[MongoStorageSettings.SectionName] = settings[MongoStorageSettings.SectionName];
            File.WriteAllText(config, new SerializerBuilder().Build().Serialize(document));
        }

        var unavailableServer = new TcpListener(IPAddress.Loopback, 0);
        unavailableServer.Start();
        try
        {
            var endpoint = unavailableServer.LocalEndpoint.ToString();
            var yaml = $$"""
                mongo:
                  endpoint: "{{endpoint}}"
                  databaseName: "{{DatabaseName}}"
                  authenticationDatabase: "{{AuthenticationDatabase}}"
                  userName: "{{UserName}}"
                  serverSelectionTimeoutSeconds: {{SelectionTimeoutSeconds}}
                """;
            WriteSettings(yaml);
            var settings = MongoStorageSettings.Load(config);
            using (var client = MongoStorage.CreateClient(settings, TestPassword))
            {
                if (client.Settings.Server.ToString() != endpoint ||
                    client.Settings.Credential.Source != AuthenticationDatabase ||
                    client.Settings.Credential.Username != UserName ||
                    client.GetDatabase(settings.DatabaseName).DatabaseNamespace.DatabaseName != DatabaseName ||
                    client.Settings.ServerSelectionTimeout != TimeSpan.FromSeconds(SelectionTimeoutSeconds))
                {
                    throw new Exception("MongoDB ignored its configured endpoint, database, authentication or timeout.");
                }

                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                await ExpectAsync<OperationCanceledException>(() =>
                    MongoStorage.CheckConnectionAsync(client.GetDatabase(settings.DatabaseName), cancelled.Token));

                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));
                await ExpectAsync<TimeoutException>(() =>
                    MongoStorage.CheckConnectionAsync(client.GetDatabase(settings.DatabaseName), deadline.Token));
            }

            await ExpectAsync<ArgumentException>(() => Task.FromResult(MongoStorage.CreateClient(settings, "")));
            WriteSettings(yaml.Replace($"databaseName: \"{DatabaseName}\"", "databaseName: \"\""));
            await ExpectAsync<ArgumentException>(() => Task.FromResult(MongoStorageSettings.Load(config)));
            WriteSettings(yaml.Replace($"serverSelectionTimeoutSeconds: {SelectionTimeoutSeconds}", "serverSelectionTimeoutSeconds: 0"));
            await ExpectAsync<ArgumentOutOfRangeException>(() => Task.FromResult(MongoStorageSettings.Load(config)));
            Console.WriteLine("PASS independent MongoDB configuration, credential validation, cancellation and connection failure.");
        }
        finally
        {
            unavailableServer.Stop();
            File.Delete(config);
        }
    }

    private static async Task ExpectAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected {typeof(TException).Name}.");
    }
}
