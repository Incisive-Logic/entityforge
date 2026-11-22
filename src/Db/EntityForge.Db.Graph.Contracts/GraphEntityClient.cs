using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Graph application preparation, execution and retained reads.</summary>
public interface IGraphEntityClient : IEntityOperationClient<GraphEntityRequest, GraphEntityWrite, DatabaseCommitReceipt>
{
    /// <summary>Gets the destination incarnation.</summary>
    Guid DatabaseIncarnation
    {
        get;
    }

    /// <summary>Applies native incremental changes or explicitly scoped complete replacements.</summary>
    /// <param name="session">The captured changes and their operation identity.</param>
    /// <param name="cancellationToken">Cancels waiting for delivery. Cancellation does not prove that the operation was rolled back.</param>
    /// <returns>The authoritative commit receipt, including a retained result when a retry is recognized.</returns>
    ValueTask<DatabaseCommitReceipt> ApplyAsync(GraphChangeSession session, CancellationToken cancellationToken = default);

    /// <summary>Selects a frozen native write against database-owned state; it does not bind or execute it.</summary>
    /// <param name="request">The captured values, destination identity and mapping interpretation.</param>
    /// <param name="cancellationToken">Cancels preparation.</param>
    /// <returns>A prepared write owned by the caller. Dispose it asynchronously when no longer needed to release retained resources.</returns>
    ValueTask<GraphEntityWrite> PrepareAsync(GraphEntityRequest request, CancellationToken cancellationToken = default);

    /// <summary>Prepares one ordered batch under one receiving-side snapshot. Requests must share destination, operation, map and version guard.</summary>
    /// <param name="requests">The ordered captured operations to prepare together.</param>
    /// <param name="cancellationToken">Cancels preparation.</param>
    /// <returns>One prepared write for the batch. The caller must dispose it asynchronously.</returns>
    ValueTask<GraphEntityWrite> PrepareBatchAsync(IReadOnlyList<GraphEntityRequest> requests, CancellationToken cancellationToken = default);

    /// <summary>Validates an established mapping and retains one view for recursive materialization. Must not register or change mappings.</summary>
    /// <param name="manifest">The already established mapping contract used for the reads.</param>
    /// <param name="cancellationToken">Cancels acquisition of the read scope.</param>
    /// <returns>A sequential read scope owned by the caller. Dispose it asynchronously to release the retained view.</returns>
    ValueTask<IGraphEntityReadScope> OpenReadAsync(EntityMappingManifest manifest, CancellationToken cancellationToken = default);
}

/// <summary>One sequential retained Graph view. Unavailable state must throw, never fall back to absence or current data.</summary>
public interface IGraphEntityReadScope : IAsyncDisposable
{
    /// <summary>Gets the immutable retained version.</summary>
    DatabaseVersion Version
    {
        get;
    }

    /// <summary>Reads a vertex at that version.</summary>
    /// <param name="identity">The vertex to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The vertex at the retained version, or null if it is absent. Unavailable retained state is an error, not absence.</returns>
    ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId identity, CancellationToken cancellationToken = default);

    /// <summary>Reads outgoing edges at that version.</summary>
    /// <param name="identity">The source vertex identity.</param>
    /// <param name="label">The relationship label to select.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching edges at the retained version. An empty list means no matching edges, not unavailable state.</returns>
    ValueTask<IReadOnlyList<GraphEdge>> OutgoingAsync(GraphVertexId identity, string label, CancellationToken cancellationToken = default);

    /// <summary>Checks availability before a completed read is returned.</summary>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <returns>A task that completes when the retained view is still available; invalidated views throw.</returns>
    ValueTask ValidateAsync(CancellationToken cancellationToken = default);
}

// Receiver-side optimization; does not grant concurrent access to the public sequential scope.
internal interface IGraphEntityBatchReadScope
{
    ValueTask PrefetchVerticesAsync(IReadOnlyList<GraphVertexId> identities, CancellationToken token);
}
