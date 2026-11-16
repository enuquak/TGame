using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode(string name, ServerGroup group, RpcDispatcher messages, IIdGenerator idGenerator, bool diagnosticsEnabled = false)
    : ServerNode(name, group, messages, idGenerator);
