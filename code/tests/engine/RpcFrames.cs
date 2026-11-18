using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using TGame.Transport;

internal static class RpcFrames
{
    public static Frame Request(IMessage message, string requestId) =>
        new() { Request = new RpcRequest { RequestId = requestId, Payload = Any.Pack(message) } };

    public static Frame Reply(IMessage message, string requestId) =>
        new() { Reply = new RpcReply { RequestId = requestId, Payload = Any.Pack(message) } };
}
