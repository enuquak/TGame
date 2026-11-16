using TGame.Tests;
using System.Globalization;
using TGame.Server.Engine;
using TGame.Server;

const int MaxIdBatchSize = 100000;

if (args is ["--ids", var configPath, var serverConfigPath, var countText])
{
    if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count is < 1 or > MaxIdBatchSize)
    {
        throw new ArgumentException($"ID count must be between 1 and {MaxIdBatchSize}.");
    }

    var settings = ServerSettings.Load(serverConfigPath);
    var options = IdGeneratorOptions.Load(configPath, settings.ZooKeeper);
    await using var idGenerator = await SnowflakeIdGenerator.CreateAsync(options);
    for (int i = 0; i < count; i++)
    {
        Console.WriteLine(idGenerator.GenId().ToString(CultureInfo.InvariantCulture));
    }

    return 0;
}

if (args is not [var directory, var endpoint, var pin])
{
    Console.Error.WriteLine("Usage: TGame.ProbeClient <run-directory> <wss-endpoint> <certificate-sha256>");
    return 1;
}

await ProbeClient.RunAsync(directory, new Uri(endpoint), pin);
return 0;
