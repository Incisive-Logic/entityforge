using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal interface IRdfBoundedSnapshotReader
{
    IReadOnlyList<RdfQuad> FindBounded(RdfQuadPattern pattern, RdfSnapshotLease snapshot, int maximumResults,
        CancellationToken cancellationToken);
}

/// <summary>Reads native RDF patterns and bounded SPARQL queries.</summary>
public interface IRdfQueryService
{
    /// <summary>Finds all matching quads as a small-result convenience; use <see cref="QueryPageAsync"/> for untrusted cardinality.</summary>
    /// <param name="pattern">Term and graph constraints to apply to the currently published view.</param>
    /// <returns>The matching quads materialized in memory.</returns>
    /// <remarks>This matches stored RDF patterns, not SPARQL text. Use an explicit snapshot lease when multiple calls must share one retained view.</remarks>
    IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern);

    /// <summary>Reads a bounded cursor-backed quad page.</summary>
    /// <param name="pattern">The quad pattern; every continuation request must use the same pattern.</param>
    /// <param name="pageSize">Maximum quads returned, from 1 through 1,000 inclusive.</param>
    /// <param name="continuation">An active opaque continuation from this database instance, or null to begin a new read.</param>
    /// <param name="cancellationToken">Cancels admission and supported page-read stages.</param>
    /// <returns>Quads from one retained physical snapshot, its version, and a continuation/expiry when more pages remain.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page size is outside the supported range.</exception>
    /// <exception cref="RdfQueryContinuationException">The continuation is invalid, expired or belongs to another pattern.</exception>
    /// <exception cref="RdfQueryCapacityException">The configured retained-continuation capacity is exhausted.</exception>
    /// <remarks>Continue until <see cref="RdfQueryPage.NextContinuation"/> is null, even if a page contains no quads. Continuations advance server-side state; they are not replay tokens for the last page. The token is scoped to the database instance and does not survive reopening it. Each successful continuation read refreshes its idle expiry. Completion, explicit release, expiry cleanup or database disposal releases the retained read. Finish or release reads promptly. After an ambiguous lost page response, restart enumeration rather than assume retrying the token returns that page again. A page's version identifies its view but does not itself retain that view.</remarks>
    ValueTask<RdfQueryPage> QueryPageAsync(RdfQuadPattern pattern, int pageSize,
        string? continuation = null, CancellationToken cancellationToken = default);

    /// <summary>Releases a paged read's retained snapshot without fetching the remaining pages.</summary>
    /// <param name="continuation">The opaque continuation returned by a query page.</param>
    /// <param name="cancellation">Cancellation while waiting to release the read.</param>
    /// <returns>True if a retained continuation was removed; false if the token is malformed, unknown or already released.</returns>
    /// <remarks>An expired continuation may still be retained pending cleanup; releasing it can return true. Cancellation while waiting for access does not establish that release completed.</remarks>
    ValueTask<bool> ReleaseQueryContinuationAsync(string continuation, CancellationToken cancellation = default);

    /// <summary>Executes the supported bounded SPARQL query surface.</summary>
    /// <param name="query">SPARQL query text supported by this database's query implementation.</param>
    /// <param name="cancellationToken">Cancels supported query stages and configured outbound requests.</param>
    /// <returns>The query-form-specific bindings, boolean or constructed/described quads.</returns>
    /// <remarks>This is a buffered result, not the hosted streaming scan protocol. The implementation acquires and releases the query's local snapshot lease, including on failure; retaining the result does not retain database history. Its snapshot version describes local data only: remote SERVICE responses are outside that snapshot's isolation guarantees. Unsupported forms and exhausted budgets fail explicitly; this method does not assert full SPARQL conformance.</remarks>
    ValueTask<RdfSparqlQueryResult> QuerySparqlAsync(string query,
        CancellationToken cancellationToken = default);

    /// <summary>Executes a safely parameterised bounded SPARQL query.</summary>
    /// <param name="request">Query text, typed RDF-term bindings and an optional retained local snapshot version.</param>
    /// <param name="cancellationToken">Cancels supported query stages.</param>
    /// <returns>The buffered query-form-specific result at the requested retained version, or the currently published version captured for the query.</returns>
    /// <remarks>Use typed bindings for values rather than concatenating user text into the query. Bindings are rendered before snapshot acquisition. An unavailable requested version fails rather than falling back to current data. The query releases its own snapshot lease after evaluation; the result does not retain history. Remote SERVICE data remains independent of the local snapshot.</remarks>
    ValueTask<RdfSparqlQueryResult> QuerySparqlAsync(RdfSparqlQueryRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Applies native RDF mutations, document pages and SPARQL Update commands.</summary>
public interface IRdfUpdateService
{
    /// <summary>Atomically commits RDF assertions and retractions.</summary>
    /// <param name="transaction">Captured mutations, transaction identity and any optimistic read predicates.</param>
    /// <param name="cancellationToken">Cancels admission and supported commit stages; it does not promise rollback after durable commitment.</param>
    /// <returns>The committed version, including the original version for a retained idempotent replay.</returns>
    /// <remarks>Preserve the identity and captured payload when resolving an ambiguous response. Replay recognition is bounded by operation retention.</remarks>
    ValueTask<DatabaseVersion> CommitAsync(RdfTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically commits RDF mutations and returns the outcome and change-feed boundary.</summary>
    /// <param name="transaction">The atomic mutation batch and any optimistic read dependencies.</param>
    /// <param name="cancellationToken">Cancels admission and supported commit stages.</param>
    /// <returns>A receipt describing a new commit or recognized replay, its version and change cursor.</returns>
    /// <remarks>An interrupted wait is not evidence that the write did not commit. A commit receipt describes a write outcome, not a query snapshot lease.</remarks>
    ValueTask<DatabaseCommitReceipt> CommitWithReceiptAsync(RdfTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>Executes and commits a bounded SPARQL Update.</summary>
    /// <param name="transactionId">Nonempty identity retained for command replay.</param>
    /// <param name="update">Supported SPARQL Update text. Operations in one sequence see preceding operations and publish atomically.</param>
    /// <param name="cancellationToken">Cancels supported preparation, outbound retrieval and commit stages.</param>
    /// <returns>The committed version and mutation count, or the retained result for an identical replay.</returns>
    /// <remarks>Retry with the same identity and exact update text. A changed command is not the same operation. Cancellation does not guarantee rollback after commitment.</remarks>
    ValueTask<RdfSparqlUpdateCommit> CommitSparqlUpdateAsync(Guid transactionId, string update,
        CancellationToken cancellationToken = default);

    /// <summary>Executes a safely parameterised, replayable SPARQL Update.</summary>
    /// <param name="request">Transaction identity, update text and typed RDF-term bindings.</param>
    /// <param name="cancellationToken">Cancels supported preparation and commit stages.</param>
    /// <returns>The committed or retained replay result.</returns>
    /// <remarks>Preserve both text and binding values when retrying; replay checks apply to the rendered command.</remarks>
    ValueTask<RdfSparqlUpdateCommit> CommitSparqlUpdateAsync(RdfSparqlUpdateRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Commits one idempotent page of an RDF document.</summary>
    /// <param name="documentId">Stable source-document identity shared by all of its pages and retries.</param>
    /// <param name="page">A nonempty bounded page with a stable zero-based page number and captured quads.</param>
    /// <param name="cancellationToken">Cancels admission and supported commit stages.</param>
    /// <returns>The document/page identity, committed version and commit or replay outcome.</returns>
    /// <remarks>Document identity and page number derive the transaction identity. Reuse them only with the identical page payload. Each page commits independently; multiple pages are not one atomic document transaction. Replay recognition is bounded by operation retention.</remarks>
    ValueTask<RdfDocumentPageCommit> CommitDocumentPageAsync(RdfDocumentIngestId documentId,
        RdfDocumentPage page, CancellationToken cancellationToken = default);

    /// <summary>Builds and publishes an initial RDF dataset in an empty database.</summary>
    /// <param name="transactionId">Nonempty identity for this load; preserve it and the input when retrying an ambiguous outcome.</param>
    /// <param name="quads">Nonempty initial dataset. Duplicate quads are collapsed. Do not mutate the input during the call.</param>
    /// <param name="cancellationToken">Cancels admission and supported preparation/build stages; cancellation does not imply rollback after a durable commit.</param>
    /// <returns>The published database version, or the original version for a recognized replay.</returns>
    /// <exception cref="RdfBulkLoadBudgetException">Input preparation exceeds the configured quad or charged-byte budget; nothing from this load is published.</exception>
    /// <remarks>Input capture is serialized with writes and observes cancellation between quads. This API materializes the initial dataset; it is not a bounded-memory streaming importer. For incremental ingestion, use document pages, each of which commits separately.</remarks>
    ValueTask<DatabaseVersion> BulkLoadAsync(Guid transactionId, IReadOnlyList<RdfQuad> quads,
        CancellationToken cancellationToken = default);
}

/// <summary>Acquires and reads retained RDF snapshots.</summary>
public interface IRdfSnapshotService
{
    /// <summary>Acquires a retained RDF snapshot.</summary>
    /// <param name="version">A retained version, or null for the currently published version.</param>
    /// <returns>A lease owned by this database instance; dispose it when all reads finish.</returns>
    /// <remarks>A version number alone does not retain history. A lease from another instance, including an earlier opening of the same directory, cannot protect this database's reads.</remarks>
    RdfSnapshotLease AcquireSnapshot(DatabaseVersion? version = null);

    /// <summary>Awaits publication and retains the requested view without blocking a worker.</summary>
    /// <param name="version">A retained version, or null for the version published when acquisition completes.</param>
    /// <param name="cancellationToken">Cancels waiting for publication.</param>
    /// <returns>A disposable lease owned by this database instance.</returns>
    ValueTask<RdfSnapshotLease> AcquireSnapshotAsync(DatabaseVersion? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>Finds quads within a retained RDF snapshot.</summary>
    /// <param name="pattern">The quad pattern to evaluate.</param>
    /// <param name="snapshot">An active snapshot lease acquired from this database instance.</param>
    /// <returns>All matching quads from the retained view; use paged reads for untrusted result cardinality.</returns>
    /// <exception cref="ArgumentNullException">The snapshot is null.</exception>
    /// <exception cref="ArgumentException">The lease belongs to a different database instance.</exception>
    /// <exception cref="ObjectDisposedException">The lease is inactive or its timestamp projection context has ended.</exception>
    IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern, RdfSnapshotLease snapshot);
}

/// <summary>Validates RDF data against explicit shapes.</summary>
public interface IRdfValidationService
{
    /// <summary>Validates a retained default-graph snapshot against the supported SHACL Core shapes.</summary>
    /// <param name="shapes">Explicit shape quads to evaluate; this does not install a persistent write constraint.</param>
    /// <param name="maximumResults">Bound on returned validation findings.</param>
    /// <param name="snapshot">A retained database version, or null for the currently published version.</param>
    /// <param name="cancellationToken">Cancellation checked around bounded validation.</param>
    /// <returns>A report identifying the validated snapshot and its findings.</returns>
    /// <remarks>Validation does not mutate the dataset. Data, shape and result limits remain applicable; this is not an unrestricted SHACL engine.</remarks>
    RdfShaclValidationReport ValidateShacl(IReadOnlyList<RdfQuad> shapes,
        int maximumResults = RdfShacl.MaximumResults, DatabaseVersion? snapshot = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Calculates bounded RDF entailments.</summary>
public interface IRdfEntailmentService
{
    /// <summary>Calculates bounded RDFS consequences for a retained snapshot.</summary>
    /// <param name="maximumResults">Bound on derived quads.</param>
    /// <param name="snapshot">A retained version, or null for the currently published version.</param>
    /// <param name="cancellationToken">Cancellation checked around bounded inference.</param>
    /// <returns>The supported RDFS consequences and the source snapshot identity.</returns>
    /// <remarks>Reads the default graph without persisting inferred quads. This does not enable an entailment regime for subsequent SPARQL requests.</remarks>
    RdfEntailmentResult InferRdfs(int maximumResults = RdfEntailment.MaximumDerivedQuads,
        DatabaseVersion? snapshot = null, CancellationToken cancellationToken = default);
}

/// <summary>Reads and subscribes to committed RDF changes.</summary>
public interface IRdfChangeFeed
{
    /// <summary>Returns the retained RDF change-feed bounds.</summary>
    /// <param name="cancellationToken">Cancels the bounds read.</param>
    /// <returns>The current retained cursor bounds; retention can advance after this call.</returns>
    ValueTask<DatabaseChangeFeedBounds> GetChangeFeedBoundsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Reads committed RDF changes after a durable cursor.</summary>
    /// <param name="cursor">Exclusive preceding cursor, or null to begin at the retained feed.</param>
    /// <param name="selector">The committed changes to include.</param>
    /// <param name="maximumCount">Maximum changes requested in the page.</param>
    /// <param name="cancellationToken">Cancels the page read.</param>
    /// <returns>A page of retained logical changes and its continuation information.</returns>
    /// <remarks>A cursor does not retain history. A cursor that falls outside retention requires resynchronization rather than silently accepting a gap.</remarks>
    ValueTask<DatabaseChangePage> ReadChangePageAfterAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>Streams retained and subsequently committed RDF changes after a cursor.</summary>
    /// <param name="cursor">Exclusive preceding cursor, or null to start at the retained feed.</param>
    /// <param name="selector">The committed changes to include.</param>
    /// <param name="options">Paging and polling behaviour for the subscription.</param>
    /// <param name="cancellationToken">Stops subscription reads and waiting.</param>
    /// <returns>An asynchronous sequence of committed logical change sets.</returns>
    /// <remarks>Dispose the enumerator when abandoning the subscription. A snapshot lease does not preserve change-feed retention.</remarks>
    IAsyncEnumerable<CommittedChangeSet> SubscribeToChangesAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Runs explicit RDF storage maintenance.</summary>
public interface IRdfMaintenanceService
{
    /// <summary>Creates a durable RDF checkpoint.</summary>
    /// <param name="cancellationToken">Cancels supported checkpoint stages.</param>
    /// <returns>The version covered by the published checkpoint.</returns>
    /// <remarks>A checkpoint is storage recovery state, not an independently restorable backup archive.</remarks>
    ValueTask<DatabaseVersion> CreateCheckpointAsync(CancellationToken cancellationToken = default);

    /// <summary>Rebuilds and publishes the RDF physical access paths.</summary>
    /// <param name="cancellationToken">Cancels supported replacement preparation and publication stages.</param>
    /// <returns>The version at which the replacement is published.</returns>
    /// <remarks>Logical RDF data is preserved. Concurrent writes may require another preparation attempt; failure to converge is reported rather than publishing a stale replacement.</remarks>
    ValueTask<DatabaseVersion> ReindexAsync(CancellationToken cancellationToken = default);
}

/// <summary>Aggregates the supported embedded RDF dataset capabilities without exposing storage composition.</summary>
public interface IRdfDatabase : IDatabaseOperationalStatus, IRdfQueryService, IRdfUpdateService,
    IRdfSnapshotService, IRdfValidationService, IRdfEntailmentService, IRdfChangeFeed,
    IRdfMaintenanceService, IAsyncDisposable;
