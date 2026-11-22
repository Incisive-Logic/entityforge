using EntityForge.Db.Graph.Contracts;
using EntityForge.Db;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;

namespace EntityForge.Db.Graph;

/// <summary>
/// Defines the supported embedded native-graph capabilities without exposing physical storage services.
/// </summary>
public interface IGraphDatabase : IDatabaseOperationalStatus, IAsyncDisposable
{
    /// <summary>Commits a graph transaction and returns its database version.</summary>
    /// <param name="transaction">Ordered atomic mutations and any explicit snapshot read dependencies.</param>
    /// <param name="cancellationToken">Cancellation for admission and supported cancellable stages; cancellation does not promise rollback of a committed write.</param>
    /// <returns>The committed version, including the original version for a retained idempotent replay.</returns>
    /// <remarks>Reuse the transaction identity only with the same captured payload. Retry recognition is bounded by operation retention; do not interpret an interrupted wait as proof that nothing committed.</remarks>
    ValueTask<DatabaseVersion> CommitAsync(GraphTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>Commits a graph transaction and returns its outcome and change-feed boundary.</summary>
    /// <param name="transaction">Ordered atomic mutations and any explicit snapshot read dependencies.</param>
    /// <param name="cancellationToken">Cancellation for admission and supported cancellable stages.</param>
    /// <returns>The commit or retained replay receipt, with its version and change cursor.</returns>
    /// <remarks>Unlike a query result, a receipt describes a write outcome. Preserve transaction identity and payload when resolving an ambiguous response.</remarks>
    ValueTask<DatabaseCommitReceipt> CommitWithReceiptAsync(GraphTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>Directly builds and atomically publishes a complete initial graph into an empty database.</summary>
    /// <remarks>
    /// Preparation is serialized and bounded before materializing native mutations. The current
    /// implementation admits at most 100,000 charged work items and 64 MiB of charged input;
    /// vertices, labels and properties each cost an item, and edges cost five for adjacency work.
    /// Byte charging includes serialized contents and 128 bytes per item. Private initial page
    /// images have a separate 64 MiB limit. These are admission limits, not total-process RSS limits.
    /// </remarks>
    ValueTask<DatabaseVersion> BulkLoadAsync(Guid transactionId,
        IReadOnlyList<GraphVertex> vertices, IReadOnlyList<GraphEdge> edges,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the currently retained committed-change cursor bounds.</summary>
    ValueTask<DatabaseChangeFeedBounds> GetChangeFeedBoundsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads committed logical changes strictly after the supplied cursor.</summary>
    ValueTask<DatabaseChangePage> ReadChangePageAfterAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Streams retained and subsequently committed logical changes after a cursor.</summary>
    IAsyncEnumerable<CommittedChangeSet> SubscribeToChangesAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically acquires a leased logical snapshot and its gap-free change-feed boundary.</summary>
    /// <param name="cancellationToken">Cancels acquisition before a bootstrap is returned.</param>
    /// <returns>A bootstrap whose lease the caller must dispose after snapshot enumeration.</returns>
    /// <remarks>The lease retains snapshot state, not change-feed history. Resume changes after the returned boundary; if that boundary expires under change retention, bootstrap again rather than silently skipping changes.</remarks>
    ValueTask<GraphChangeBootstrap> AcquireChangeBootstrapAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Reads one bounded page from a leased logical snapshot.</summary>
    /// <param name="lease">An active lease acquired from this database; keep it alive across every page.</param>
    /// <param name="cursor">The preceding page's opaque continuation, or null for the first page.</param>
    /// <param name="maximumCount">Maximum logical entities per page, from 1 through 1,000 inclusive; defaults to 256.</param>
    /// <param name="cancellationToken">Cancellation checked before snapshot enumeration begins.</param>
    /// <returns>Snapshot entities represented as creation effects and a continuation, or a null continuation at completion.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The requested page size is outside 1 through 1,000.</exception>
    /// <exception cref="ArgumentException">The lease belongs to another database or the continuation is invalid for this snapshot.</exception>
    /// <exception cref="ObjectDisposedException">The lease has been released.</exception>
    /// <remarks>Follow continuations until null to read the complete snapshot; the page limit does not truncate the dataset. Vertices precede edges. Continuations are scoped to the database instance and snapshot version and must not be fabricated or reused after reopen. This enumerates stored graph state, not a GQL result or a historical change feed.</remarks>
    ValueTask<GraphLogicalSnapshotPage> ReadSnapshotPageAsync(GraphSnapshotLease lease,
        GraphSnapshotCursor? cursor = null, int maximumCount = 256,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a vertex from the current snapshot, or <see langword="null"/> when absent.</summary>
    GraphVertex? GetVertex(GraphVertexId id);

    /// <summary>Returns a vertex as it existed at a retained database version.</summary>
    GraphVertex? GetVertex(GraphVertexId id, DatabaseVersion asOf);

    /// <summary>Awaits publication without blocking a worker, then reads a vertex at the retained version.</summary>
    ValueTask<GraphVertex?> GetVertexAsync(GraphVertexId id, DatabaseVersion asOf,
        CancellationToken cancellationToken = default);

    /// <summary>Returns an edge from the current snapshot, or <see langword="null"/> when absent.</summary>
    GraphEdge? GetEdge(GraphEdgeId id);

    /// <summary>Returns an edge as it existed at a retained database version.</summary>
    GraphEdge? GetEdge(GraphEdgeId id, DatabaseVersion asOf);

    /// <summary>Awaits publication without blocking a worker, then reads an edge at the retained version.</summary>
    ValueTask<GraphEdge?> GetEdgeAsync(GraphEdgeId id, DatabaseVersion asOf,
        CancellationToken cancellationToken = default);

    /// <summary>Returns all current outgoing edges as a small-result convenience; use bounded graph queries for untrusted cardinality.</summary>
    IReadOnlyList<GraphEdge> Outgoing(GraphVertexId id, string? label = null);

    /// <summary>Returns flattened current outgoing edges for several vertices in one bounded lookup.</summary>
    IReadOnlyList<GraphEdge> Outgoing(IReadOnlyList<GraphVertexId> ids, string? label = null);

    /// <summary>Returns outgoing edges at a retained database version.</summary>
    IReadOnlyList<GraphEdge> Outgoing(GraphVertexId id, DatabaseVersion asOf, string? label = null);

    /// <summary>Awaits publication without blocking a worker, then reads outgoing edges at the retained version.</summary>
    ValueTask<IReadOnlyList<GraphEdge>> OutgoingAsync(GraphVertexId id, DatabaseVersion asOf,
        string? label = null, CancellationToken cancellationToken = default);

    /// <summary>Returns all current incoming edges as a small-result convenience; use bounded graph queries for untrusted cardinality.</summary>
    IReadOnlyList<GraphEdge> Incoming(GraphVertexId id, string? label = null);

    /// <summary>Returns flattened current incoming edges for several vertices in one bounded lookup.</summary>
    IReadOnlyList<GraphEdge> Incoming(IReadOnlyList<GraphVertexId> ids, string? label = null);

    /// <summary>Returns incoming edges at a retained database version.</summary>
    IReadOnlyList<GraphEdge> Incoming(GraphVertexId id, DatabaseVersion asOf, string? label = null);

    /// <summary>Executes a neutral bounded graph pattern at a stable snapshot.</summary>
    /// <param name="query">The structured graph pattern; this is not GQL source text.</param>
    /// <param name="asOf">A retained version, or null to capture the currently published version.</param>
    /// <param name="cancellationToken">Cancellation observed during supported query execution stages.</param>
    /// <returns>Matches and supporting graph data from the same captured snapshot.</returns>
    /// <remarks>The method holds its snapshot lease for execution and releases it before returning. Returning a version does not reserve that version for a later request.</remarks>
    ValueTask<QueryResultEnvelope> QueryAsync(BoundedGraphPatternQuery query,
        DatabaseVersion? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Explains a neutral bounded graph pattern and optionally measures its execution.</summary>
    /// <param name="query">The structured graph pattern to plan; this is not GQL source text.</param>
    /// <param name="analyze">Whether to execute the read query and include observed row counts and timings. False requests estimates without running the pattern.</param>
    /// <param name="asOf">A retained version, or null to capture the currently published version.</param>
    /// <param name="cancellationToken">Cancellation observed during supported execution stages when analysis is requested.</param>
    /// <returns>The captured snapshot, planning estimates and, when requested, execution measurements.</returns>
    /// <remarks>
    /// Planning and optional execution use the same captured snapshot. Analysis performs
    /// real query work and is subject to the query's execution limits; it is not a cost-free
    /// estimate. Timings describe this invocation, not an application latency guarantee.
    /// The implementation releases its snapshot lease before returning. An unavailable
    /// requested version fails rather than selecting a different snapshot.
    /// </remarks>
    ValueTask<GraphPatternQueryExplanation> ExplainAsync(BoundedGraphPatternQuery query,
        bool analyze = false, DatabaseVersion? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Acquires a lease that prevents the selected snapshot from being reclaimed.</summary>
    /// <param name="version">A retained version, or null for the currently published version.</param>
    /// <returns>A disposable retention lease belonging to this database instance.</returns>
    /// <remarks>This does not create historical state that has already been reclaimed. The local implementation normally retains only the latest version for new acquisitions; existing leases protect older state. Release leases promptly to permit reclamation and maintenance.</remarks>
    GraphSnapshotLease AcquireSnapshot(DatabaseVersion? version = null);

    /// <summary>Rejects a snapshot lease which is foreign or no longer active.</summary>
    /// <param name="lease">The lease to validate against this database instance.</param>
    /// <exception cref="ArgumentNullException">The lease is null.</exception>
    /// <exception cref="ArgumentException">The lease belongs to another database.</exception>
    /// <exception cref="ObjectDisposedException">The lease is no longer active.</exception>
    void ValidateSnapshot(GraphSnapshotLease lease);

    /// <summary>Awaits completed publication and retains the requested snapshot without blocking a worker.</summary>
    /// <param name="version">A retained version, or null for the version published when acquisition completes.</param>
    /// <param name="cancellationToken">Cancels waiting for publication.</param>
    /// <returns>A disposable retention lease; the caller owns its lifetime.</returns>
    ValueTask<GraphSnapshotLease> AcquireSnapshotAsync(DatabaseVersion? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>Creates a durable checkpoint and returns the checkpointed database version.</summary>
    ValueTask<DatabaseVersion> CreateCheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>Builds and atomically publishes a complete replacement of the current Graph access path.</summary>
    ValueTask<DatabaseVersion> ReindexAsync(CancellationToken cancellationToken = default);
}
