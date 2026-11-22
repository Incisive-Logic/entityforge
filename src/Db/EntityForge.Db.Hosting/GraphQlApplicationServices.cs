using EntityForge.Db.Client;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting;

/// <summary>Application operations required by GraphQL resolvers.</summary>
internal interface IGraphQlApplicationServices
{
    DatabaseProfileId Profile
    {
        get;
    }

    ISemanticDatabase Semantic
    {
        get;
    }

    ValueTask<EntityProjectionQueryResult> QueryAsync(EntityProjectionQueryRequest request, CancellationToken cancellation);

    ValueTask<DatabaseCommitReceipt> CommitGraphAsync(GraphTransaction transaction, CancellationToken cancellation);

    ValueTask<DatabaseCommitReceipt> CommitRdfAsync(RdfTransaction transaction, CancellationToken cancellation);
}

/// <summary>Connects GraphQL application operations to the selected hosted database.</summary>
internal sealed class HostedGraphQlApplicationServices(EntityForgeDbRuntime runtime) : IGraphQlApplicationServices
{
    public DatabaseProfileId Profile => runtime.Profile;

    public ISemanticDatabase Semantic => runtime.Database;

    public ValueTask<EntityProjectionQueryResult> QueryAsync(EntityProjectionQueryRequest request, CancellationToken cancellation) =>
        EntityProjectionTransportEndpoints.Client(runtime).QueryAsync(request, cancellation);

    public ValueTask<DatabaseCommitReceipt> CommitGraphAsync(GraphTransaction transaction, CancellationToken cancellation) =>
        runtime.GraphDatabase.CommitWithReceiptAsync(transaction, cancellation);

    public ValueTask<DatabaseCommitReceipt> CommitRdfAsync(RdfTransaction transaction, CancellationToken cancellation) =>
        runtime.RdfDatabase.CommitWithReceiptAsync(transaction, cancellation);
}
