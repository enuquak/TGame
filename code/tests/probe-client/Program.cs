using TGame.Tests;

if (args is not [var directory, var endpoint, var pin])
{
    Console.Error.WriteLine("Usage: TGame.ProbeClient <run-directory> <wss-endpoint> <certificate-sha256>");
    return 1;
}

await ProbeClient.RunAsync(directory, new Uri(endpoint), pin);
return 0;
