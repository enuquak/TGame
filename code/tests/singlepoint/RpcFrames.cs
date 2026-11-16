using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using TGame.Profile;
using TGame.Transport;

internal static class RpcFrames
{
    public static Frame Request(ProfileRequest message) => Request(message, message.RequestId);
    public static Frame Reply(ProfileReply message) => Reply(message, message.RequestId);

    public static Frame Request(IMessage message, string requestId) =>
        new() { Request = new RpcRequest { RequestId = requestId, Payload = Any.Pack(message) } };

    public static Frame Reply(IMessage message, string requestId) =>
        new() { Reply = new RpcReply { RequestId = requestId, Payload = Any.Pack(message) } };

    public static T ReadRequest<T>(this Frame frame) where T : IMessage<T>, new() => frame.Request.Payload.Unpack<T>();
    public static T ReadReply<T>(this Frame frame) where T : IMessage<T>, new() => frame.Reply.Payload.Unpack<T>();
}
