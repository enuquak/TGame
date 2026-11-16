using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode(string role, RpcDispatcher messages, IIdGenerator idGenerator, bool diagnosticsEnabled = false)
    : ServerNode(role, idGenerator);
