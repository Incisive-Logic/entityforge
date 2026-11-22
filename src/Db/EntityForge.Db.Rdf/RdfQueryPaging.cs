using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>One bounded page read from an exact RDF snapshot.</summary>
/// <param name="SnapshotVersion">Version of the view used to read this page; the number does not retain that view.</param>
/// <param name="Quads">Materialized quads in this page. An empty collection does not by itself indicate completion.</param>
/// <param name="NextContinuation">Opaque advancing continuation token, or null when enumeration is complete.</param>
/// <param name="ContinuationExpiresAtUtc">Idle expiry of a retained continuation, or null when enumeration is complete.</param>
/// <remarks>Pages returned by <see cref="IRdfQueryService.QueryPageAsync"/> share one retained view across continuations. The database owns that retention; this record is not a disposable snapshot lease. A final page no longer keeps the continuation alive. Manually constructing this record neither acquires a snapshot nor validates a continuation. Quad paging is separate from SPARQL result ordering and the hosted streaming scan protocol.</remarks>
public sealed record RdfQueryPage(
    long SnapshotVersion,
    IReadOnlyList<RdfQuad> Quads,
    string? NextContinuation,
    DateTimeOffset? ContinuationExpiresAtUtc);

/// <summary>Reports an invalid, expired, or mismatched RDF query continuation.</summary>
public sealed class RdfQueryContinuationException(string message) : EntityForgeDbException(message);
