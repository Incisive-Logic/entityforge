using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using EntityForge.Query.Results;

namespace EntityForge.Db.Distributed.Hosting;

internal sealed partial class PartitionApplicationClient
{
    private static PartitionProjectionValue GraphOutput(QueryResultEnvelope envelope, CompiledGraphGqlQuery compiled,
        bool includeSupportingGraph, bool stream, CancellationToken cancellation = default, bool shaped = false)
    {
        envelope = shaped ? envelope : compiled.Pipeline?.Apply(envelope, cancellation) ?? envelope;
        if (!shaped)
        {
            envelope = compiled.ApplyOutputNames(envelope);
        }
        envelope = GraphGqlCompletion.Complete(envelope, compiled.OmitsResult);
        if (!includeSupportingGraph && (compiled.Union is not null || compiled.Pipeline is not null && !compiled.ReturnsEntityGraph || compiled.Projection is not null || envelope.Result is RecordResult))
        {
            return BoundedElement(GraphPatternQueryResponseMapper.Project(envelope), stream);
        }

        return WithoutNativeSnapshot(GraphPatternQueryResponseMapper.Map(envelope, 0), stream);
    }
}
