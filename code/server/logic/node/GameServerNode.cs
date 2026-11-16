using TGame.Server.Engine;

namespace TGame.Server.Logic;

internal sealed partial class GameServerNode(string role, RpcDispatcher messages, bool diagnosticsEnabled = false) : ServerNode(role);
