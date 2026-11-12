using System.Threading.Channels;
using Grpc.Core;
using TGame.Transport;

namespace TGame.Server;

internal sealed partial class ServerNode
{
    private void ReceiveFrame(Frame frame, PeerLink link, ChannelWriter<Frame> requests)
    {
        bool diagnosticFrame = frame.BodyCase is Frame.BodyOneofCase.DiagnosticRequest or Frame.BodyOneofCase.DiagnosticReply;
        if (diagnosticFrame && !diagnosticsEnabled)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Diagnostics are disabled."));
        }

        switch (frame.BodyCase)
        {
            case Frame.BodyOneofCase.DiagnosticReply:
                ReceiveDiagnosticReply(frame, link);
                return;

            case Frame.BodyOneofCase.GmPingReply:
                link.Receive(frame, frame.GmPingReply.RequestId, frame.GmPingReply.EntityId, ServerLogEvents.GMPongRx);
                return;

            case Frame.BodyOneofCase.ProfileReply:
                link.Receive(frame, frame.ProfileReply.RequestId, frame.ProfileReply.EntityId, ServerLogEvents.ProfileReplyRx);
                return;

            case Frame.BodyOneofCase.DiagnosticRequest or Frame.BodyOneofCase.ProfileRequest or Frame.BodyOneofCase.GmPingRequest:
                if (!requests.TryWrite(frame))
                {
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "Peer request queue is full."));
                }

                return;

            default:
                throw new InvalidDataException("Invalid stream frame.");
        }
    }

    private async Task ServeFrameAsync(Frame frame, string peer, string id, PeerLink link, CancellationToken stop)
    {
        if (frame.ProfileRequest is { } request)
        {
            TraceLog.Event(ServerLogEvents.ProfileRequestRx, role, peer, request.RequestId, ProfileStore.Subject(request), id);
            await link.ReplyAsync(await RouteProfileAsync(peer, request, stop), stop);
            return;
        }

        if (frame.GmPingRequest is { } gmRequest)
        {
            TraceLog.Event(ServerLogEvents.GMPingRx, role, peer, gmRequest.RequestId, gmRequest.EntityId, id);
            await link.ReplyAsync(await RouteGmPingAsync(peer, gmRequest, stop), stop);
            return;
        }

        await ServeDiagnosticFrameAsync(frame, peer, id, link, stop);
    }
}
