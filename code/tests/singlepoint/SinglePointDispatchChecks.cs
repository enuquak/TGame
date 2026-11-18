using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using TGame;
using TGame.Server.Engine;

internal static class SinglePointDispatchChecks
{
    public static async Task RunAsync(CancellationToken stop)
    {
        await CheckSelectedAsync(stop);
        await CheckDefaultAsync(stop);
        Console.WriteLine("PASS direct SinglePoint dispatch without business registration and default rejection.");
    }

    private static async Task CheckSelectedAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeIsolated(ProcessType.Scene);
        using var node = TestNodes.Create(nameof(ProcessType.Scene));
        await node.InitializeSinglePointAsync(new SinglePointRegistry([typeof(EchoSinglePoint)]), stop);
        var point = (EchoSinglePoint)node.SinglePoint!;
        var reply = await node.CallEntityAsync<StringValue>(point.EntityId, new StringValue { Value = "local" }, stop);
        if (reply.Value != "local:handled" || point.SourceProcess != node.Name || point.TargetEntity != point.EntityId)
        {
            throw new Exception("The selected SinglePoint did not receive the request and routing context.");
        }
    }

    private static async Task CheckDefaultAsync(CancellationToken stop)
    {
        using var configuration = TestNodes.InitializeIsolated(ProcessType.Base);
        using var defaultNode = TestNodes.Create(nameof(ProcessType.Base));
        await defaultNode.InitializeSinglePointAsync(new SinglePointRegistry([]), stop);
        try
        {
            await defaultNode.CallEntityAsync<StringValue>(defaultNode.SinglePoint!.EntityId, new StringValue(), stop);
            throw new Exception("A default SinglePoint accepted an unsupported request.");
        }
        catch (RpcException error) when (error.StatusCode == StatusCode.Unimplemented)
        {
        }
    }

    [SinglePointFor(ProcessType.Scene)]
    public sealed class EchoSinglePoint : ServerSinglePoint
    {
        public string? SourceProcess { get; private set; }
        public string? TargetEntity { get; private set; }

        public override Task<IMessage> HandleRequestAsync(EntityRpcContext context, Any payload, CancellationToken stop)
        {
            SourceProcess = context.SourceProcess;
            TargetEntity = context.EntityId;
            return Task.FromResult<IMessage>(new StringValue { Value = payload.Unpack<StringValue>().Value + ":handled" });
        }
    }
}
