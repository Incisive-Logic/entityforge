using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

public sealed partial class HttpPartitionEntityClient
{
    /// <summary>Captures and saves a Semantic aggregate with a stable source observation and operation identity.</summary>
    public async ValueTask<DatabaseDistributedCommitReceipt> SaveSemanticAsync<T>(T entity, SemanticEntityWriteOperation? operation = null,
        SemanticApplicationIdentity? application = null, CancellationToken token = default) where T : class
    {
        if (Profile != DatabaseProfileId.Semantic)
        {
            throw new InvalidOperationException("Not a Semantic destination.");
        }

        var request = new HttpSemanticEntityClient(_http, Generation).EntityStore<T>(application).CaptureSave(entity, operation ?? SemanticEntityWriteOperation.Create());
        return await DeliverAutomaticSemanticAsync(request, operation is null, token);
    }

    /// <summary>Removes the Graph mapping's owned state using automatic partition routing.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> DeleteGraphAsync<T>(object identity, Guid? operationId = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new InvalidOperationException("Not a Graph destination.");
        }

        var request = new HttpGraphEntityClient(_http, Generation).EntityStore<T>().CaptureDelete(identity, new GraphEntityWriteOptions(operationId ?? EntityOperationIds.New()));
        return DeliverAutomaticAsync(request.Captured, operationId is null, token);
    }

    /// <summary>Removes the RDF mapping's owned subject statements.</summary>
    public ValueTask<DatabaseDistributedCommitReceipt> DeleteRdfAsync<T>(object identity, Guid? operationId = null, CancellationToken token = default)
    {
        if (Profile != DatabaseProfileId.Rdf)
        {
            throw new InvalidOperationException("Not an RDF destination.");
        }

        var request = new HttpRdfEntityClient(_http, Generation).EntityStore<T>().CaptureDelete(identity, new RdfEntityWriteOptions(operationId ?? EntityOperationIds.New()));
        return DeliverAutomaticAsync(request.Captured, operationId is null, token);
    }

    /// <summary>Retracts a Semantic root and its owned contribution while preserving independent sources.</summary>
    public async ValueTask<DatabaseDistributedCommitReceipt> DeleteSemanticAsync<T>(object identity, SemanticEntityWriteOperation? operation = null,
        SemanticApplicationIdentity? application = null, CancellationToken token = default) where T : class
    {
        if (Profile != DatabaseProfileId.Semantic)
        {
            throw new InvalidOperationException("Not a Semantic destination.");
        }

        var request = new HttpSemanticEntityClient(_http, Generation).EntityStore<T>(application).CaptureDelete(identity, operation ?? SemanticEntityWriteOperation.Create());
        return await DeliverAutomaticSemanticAsync(request, operation is null, token);
    }

    /// <summary>Materializes an explicitly mapped Semantic aggregate under one completed partition cut.</summary>
    public async ValueTask<PartitionReadResult<T?>> ReadSemanticAsync<T>(object identity, SemanticAggregateMap<T> map,
        SemanticApplicationIdentity? application = null, DatabasePositionVector? minimumPosition = null, CancellationToken token = default) where T : class
    {
        if (Profile != DatabaseProfileId.Semantic)
        {
            throw new InvalidOperationException("Not a Semantic destination.");
        }

        var store = new HttpSemanticEntityClient(_http, Generation).EntityStore(map, application);
        var description = store.DescribePartitionRead(identity);
        var result = await ReadMappedAsync(description.Manifests, description.Recipe, minimumPosition, token);
        await using var scope = new DetachedMappedScope(result);
        return new(await store.ReadPartitionValueAsync(identity, scope, token), result.Position);
    }
}
