using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode(string role, IIdGenerator idGenerator) : ServerNode(role, idGenerator);
