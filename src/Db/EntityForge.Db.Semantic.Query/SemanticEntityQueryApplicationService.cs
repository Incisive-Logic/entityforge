using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Semantic;
using EntityForge.Db.Client;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Semantic.Query;

/// <summary>
/// Executes semantic queries through the profile application contract.
/// Adapters compile into the neutral query model before entering this service.
/// </summary>
internal sealed class SemanticEntityQueryApplicationService(ISemanticDatabase database)
{
    public async ValueTask<CompiledHostedEntityQuery> CompileAsync(
        PortableEntityQueryRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        CompiledPortableEntityQuery compiled = new PortableEntityQueryRequestCompiler().Compile(request);
        DatabaseVersion? snapshot = await ResolveSnapshotAsync(request, cancellation).ConfigureAwait(false);
        return new(compiled.Query, compiled.ProjectionOptions, snapshot);
    }

    public async ValueTask<HostedEntityQueryResult> ExecuteAsync(
        CompiledHostedEntityQuery query, CancellationToken cancellation)
    {
        SemanticEntityQueryResult result = await database.QueryEntitiesAsync(
            query.Query, query.ProjectionOptions, query.RequestedSnapshot, cancellation).ConfigureAwait(false);
        return new(result.Projection, result.SnapshotVersion);
    }

    public ValueTask<SemanticEntityQueryExplanation> ExplainAsync(
        CompiledHostedEntityQuery query, bool analyze, CancellationToken cancellation) =>
        database.ExplainEntitiesAsync(query.Query, query.ProjectionOptions, analyze,
            query.RequestedSnapshot, cancellation);

    public ValueTask<SemanticEntityQueryInvalidation> CheckInvalidationAsync(
        CompiledHostedEntityQuery query, DatabaseVersion observedSnapshotVersion,
        DatabaseChangeCursor? cursor, int maximumChanges, CancellationToken cancellation) =>
        database.CheckEntityQueryInvalidationAsync(query.Query, observedSnapshotVersion,
            cursor, maximumChanges, cancellation);

    public IAsyncEnumerable<SemanticEntityQuerySubscriptionEvent> SubscribeToInvalidationAsync(
        CompiledHostedEntityQuery query, DatabaseVersion observedSnapshotVersion,
        DatabaseChangeCursor? cursor, DatabaseChangeSubscriptionOptions options,
        CancellationToken cancellation) =>
        database.SubscribeToEntityQueryInvalidationAsync(query.Query, observedSnapshotVersion,
            cursor, options, cancellation);

    private async ValueTask<DatabaseVersion?> ResolveSnapshotAsync(
        PortableEntityQueryRequest request, CancellationToken cancellation)
    {
        if (request.SnapshotVersion is not null && request.SnapshotLeaseId is not null)
        {
            throw new ArgumentException("snapshotVersion and snapshotLeaseId are mutually exclusive.");
        }

        if (request.SnapshotVersion is long version)
        {
            if (version <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request.SnapshotVersion));
            }

            return new(version);
        }
        if (request.SnapshotLeaseId is Guid leaseId)
        {
            return (await database.GetSnapshotLeaseAsync(leaseId, cancellation)
                .ConfigureAwait(false)).SnapshotVersion;
        }

        return null;
    }
}
