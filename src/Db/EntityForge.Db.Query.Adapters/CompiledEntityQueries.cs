using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Query.Adapters;

internal sealed record CompiledHostedEntityQuery(
    EntityQueryDefinition Query,
    EntityProjectionProjectionOptions ProjectionOptions,
    DatabaseVersion? RequestedSnapshot);

internal sealed record HostedEntityQueryResult(
    EntityProjectionProjectionResult Projection,
    DatabaseVersion SnapshotVersion)
{
    public EntityProjectionQueryResult ToEntityProjectionResult() =>
        new(Projection.Document, SnapshotVersion, Projection.NextContinuation);

    public PortableEntityQueryResponse ToPortableResponse() =>
        PortableEntityQueryResponseMapper.Map(Projection, SnapshotVersion.Value);
}
