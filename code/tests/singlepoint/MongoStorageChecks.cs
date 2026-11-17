using System.Net;
using System.Net.Sockets;
using TGame;
using TGame.Server.Engine;
using TGame.Server.Logic;
using TGame.Server.Storage;

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
        var config = Path.Combine(AppContext.BaseDirectory, "config", "dbmgr-storage.yaml");
        var profileConfig = Path.Combine(AppContext.BaseDirectory, "config", "server.local.yaml");
        var originalConfig = File.ReadAllText(config);
        var originalProfileConfig = File.ReadAllText(profileConfig);
        var originalPassword = Environment.GetEnvironmentVariable(ServerEnvironment.DBMgrMongoPassword);
        var originalProfilePassword = Environment.GetEnvironmentVariable(ServerEnvironment.MongoPassword);
        var originalSelectedConfig = Environment.GetEnvironmentVariable(ServerEnvironment.Config);
        var unavailableServer = new TcpListener(IPAddress.Loopback, 0);
        unavailableServer.Start();

        try
        {
            var endpoint = unavailableServer.LocalEndpoint.ToString();
            var yaml = $$"""
                endpoint: "{{endpoint}}"
                databaseName: "{{DatabaseName}}"
                authenticationDatabase: "{{AuthenticationDatabase}}"
                userName: "{{UserName}}"
                serverSelectionTimeoutSeconds: {{SelectionTimeoutSeconds}}
                """;
            File.WriteAllText(config, yaml);

            var settings = MongoStorageSettings.Load(config);
            using (var client = MongoStorage.CreateClient(settings, TestPassword))
            {
                if (client.Settings.Server.ToString() != endpoint || client.Settings.Credential.Source != AuthenticationDatabase ||
                    client.Settings.Credential.Username != UserName || client.GetDatabase(settings.DatabaseName).DatabaseNamespace.DatabaseName != DatabaseName ||
                    client.Settings.ServerSelectionTimeout != TimeSpan.FromSeconds(SelectionTimeoutSeconds))
                {
                    throw new Exception("MongoDB connection ignored its configured endpoint, database, authentication or timeout.");
                }

                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await ExpectAsync<OperationCanceledException>(() => MongoStorage.CheckConnectionAsync(client.GetDatabase(settings.DatabaseName), canceled.Token));
            }

            File.WriteAllText(config, yaml.Replace($"databaseName: \"{DatabaseName}\"", "databaseName: \"\""));
            await ExpectAsync<ArgumentException>(() => Task.FromResult(MongoStorageSettings.Load(config)));
            File.WriteAllText(config, yaml.Replace($"serverSelectionTimeoutSeconds: {SelectionTimeoutSeconds}", "serverSelectionTimeoutSeconds: 0"));
            await ExpectAsync<ArgumentOutOfRangeException>(() => Task.FromResult(MongoStorageSettings.Load(config)));
            File.WriteAllText(config, yaml);

            // DBMgr must keep its own configuration and credential.
            File.WriteAllText(profileConfig, "invalid: [");
            Environment.SetEnvironmentVariable(ServerEnvironment.Config, profileConfig);
            Environment.SetEnvironmentVariable(ServerEnvironment.MongoPassword, TestPassword);
            Environment.SetEnvironmentVariable(ServerEnvironment.DBMgrMongoPassword, null);
            using (var missingCredential = new DBMgrServerSinglePoint())
            {
                await ExpectAsync<InvalidOperationException>(() => missingCredential.InitializeAsync(
                    nameof(ProcessType.DBMgr), _ => Task.CompletedTask, CancellationToken.None));
                if (missingCredential.Initialized)
                {
                    throw new Exception("DBMgr became ready without its own credential.");
                }
            }

            Environment.SetEnvironmentVariable(ServerEnvironment.DBMgrMongoPassword, TestPassword);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(TestTimeoutSeconds));
            using var unavailable = new DBMgrServerSinglePoint();
            await ExpectAsync<TimeoutException>(() => unavailable.InitializeAsync(
                nameof(ProcessType.DBMgr), _ => Task.CompletedTask, deadline.Token));
            if (unavailable.Initialized)
            {
                throw new Exception("DBMgr became ready before its database connection check succeeded.");
            }

            Console.WriteLine("PASS DBMgr storage configuration, independent credentials, cancellation and failed startup readiness.");
        }
        finally
        {
            unavailableServer.Stop();
            Environment.SetEnvironmentVariable(ServerEnvironment.Config, originalSelectedConfig);
            File.WriteAllText(config, originalConfig);
            File.WriteAllText(profileConfig, originalProfileConfig);
            Environment.SetEnvironmentVariable(ServerEnvironment.DBMgrMongoPassword, originalPassword);
            Environment.SetEnvironmentVariable(ServerEnvironment.MongoPassword, originalProfilePassword);
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
