using EntityForge.Db.Graph;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    // Internal prototype: callers retain the returned capture before submission.
    // No hidden operation-ID change is permitted after an ambiguous response.
    internal GraphEntityRequest CaptureOwnerGraphSave<T>(T entity, DistributedRoutingBinding routing)
    {
        if (Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new InvalidOperationException("Not a Graph destination.");
        }

        var store = new HttpGraphEntityClient(_http, Generation).EntityStore<T>();
        var probe = store.CaptureSave(entity, new(EntityOperationIds.New()));
        var owner = PartitionApplicationRouting.Owner(Generation, Profile, routing, probe.OperationId, probe.Export());
        return new GraphEntityRequest(probe.Captured.ForOperation(PartitionOwnerOperation.New(routing, owner)));
    }
}
