using TGame;
using TGame.Server.Engine;
using TGame.Transport;

internal static class TestNodes
{
    public static Frame Hello(string peer) => new() { Hello = peer };

    public static ServerConfiguration LoadConfiguration() =>
        ServerConfiguration.Load(Path.Combine(AppContext.BaseDirectory, "config", ServerConfiguration.FileName));
}
