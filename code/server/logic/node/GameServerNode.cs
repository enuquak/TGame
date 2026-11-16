using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode(string name, ServerGroup group, RpcDispatcher messages, bool diagnosticsEnabled = false) : ServerNode(name, group);
