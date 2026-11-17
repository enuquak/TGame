using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using TGame;
using TGame.Server.Engine;

internal abstract class TestRequestSinglePoint : ServerSinglePoint
{
    public Func<EntityRpcContext, Any, CancellationToken, Task<IMessage>>? Handler { get; set; }

    public override Task<IMessage> HandleRequestAsync(EntityRpcContext context, Any payload, CancellationToken stop)
    {
        if (Handler is null)
        {
            return base.HandleRequestAsync(context, payload, stop);
        }

        return Handler(context, payload, stop);
    }
}

[SinglePointFor(ProcessType.Login)]
internal sealed class RoutingLoginSinglePoint : TestRequestSinglePoint
{
}

[SinglePointFor(ProcessType.Base)]
internal sealed class RoutingBaseSinglePoint : TestRequestSinglePoint
{
}

[SinglePointFor(ProcessType.Profile)]
internal sealed class RoutingProfileSinglePoint : TestRequestSinglePoint
{
}
