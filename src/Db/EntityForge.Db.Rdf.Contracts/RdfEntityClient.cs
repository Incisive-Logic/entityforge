using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>RDF application preparation, execution and retained subject reads.</summary>
public interface IRdfEntityClient : IEntityOperationClient<RdfEntityRequest, RdfEntityWrite, DatabaseCommitReceipt>
{
    /// <summary>Gets the destination incarnation.</summary>
    Guid DatabaseIncarnation
    {
        get;
    }

    /// <summary>Applies a native incremental or explicitly scoped replacement session through the operation authority.</summary>
    ValueTask<DatabaseCommitReceipt> ApplyAsync(RdfChangeSession session, CancellationToken cancellationToken = default);

    /// <summary>Selects a frozen native write against database-owned state without binding or executing it.</summary>
    ValueTask<RdfEntityWrite> PrepareAsync(RdfEntityRequest request, CancellationToken cancellationToken = default);

    /// <summary>Prepares one ordered batch under one receiving-side snapshot. Requests must share destination, operation, map and version guard.</summary>
    ValueTask<RdfEntityWrite> PrepareBatchAsync(IReadOnlyList<RdfEntityRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>Validates an established mapping and retains one view. Must not register or change mappings.</summary>
    ValueTask<IRdfEntityReadScope> OpenReadAsync(EntityMappingManifest manifest, CancellationToken cancellationToken = default);
}

/// <summary>One sequential retained RDF view. Reads preserve lexical terms and default-graph scope.</summary>
public interface IRdfEntityReadScope : IAsyncDisposable
{
    /// <summary>Gets the immutable retained version.</summary>
    DatabaseVersion Version
    {
        get;
    }

    /// <summary>Reads complete default-graph subject state; unavailable state must throw.</summary>
    ValueTask<IReadOnlyList<RdfQuad>> ReadDefaultSubjectAsync(RdfIri subject, CancellationToken cancellationToken = default);

    /// <summary>Checks availability before a completed read is returned.</summary>
    ValueTask ValidateAsync(CancellationToken cancellationToken = default);
}
