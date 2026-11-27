using System;

namespace TGame.Server.Engine;

internal static class ZooKeeperPath
{
    internal static void ValidateRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!path.StartsWith('/') || path.EndsWith('/'))
        {
            throw new ArgumentException("ZooKeeper path must be absolute and have no trailing slash.");
        }

        foreach (var segment in path[1..].Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException("Invalid ZooKeeper path segment.");
            }
        }
    }
}
