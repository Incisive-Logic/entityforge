using EntityForge.Semantics;

namespace EntityForge.Query.Results;

/// <summary>An absolute URI identifying evidence associated with a query assertion.</summary>
public sealed record EvidenceReference
{
    /// <summary>Creates an evidence reference from an absolute URI.</summary>
    /// <param name="identifier">The nonnull absolute evidence identifier; no resource is fetched.</param>
    /// <exception cref="ArgumentException">The identifier is null or relative.</exception>
    public EvidenceReference(Uri identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        if (!identifier.IsAbsoluteUri)
        {
            throw new ArgumentException("An evidence identifier must be an absolute URI.", nameof(identifier));
        }

        Identifier = identifier;
    }

    /// <summary>Gets the absolute evidence identifier.</summary>
    public Uri Identifier
    {
        get;
    }
}

/// <summary>Describes the source, observation and optional validity interval of referenced evidence.</summary>
public sealed record EvidenceRecord
{
    /// <summary>Creates evidence metadata without fetching or verifying its source.</summary>
    /// <param name="reference">The nonnull reference used by result assertions.</param>
    /// <param name="providerIdentifier">The nonnull absolute URI identifying the evidence provider.</param>
    /// <param name="sourceGraph">An absolute source graph URI, or null when unspecified.</param>
    /// <param name="sourceRecordIdentifier">The provider's record identifier, when available.</param>
    /// <param name="snapshotIdentifier">The source snapshot identifier, when available.</param>
    /// <param name="observedAt">The observation time, when known.</param>
    /// <param name="validFrom">The validity start, when known.</param>
    /// <param name="validTo">The validity end, when known; must follow the start if both are specified.</param>
    /// <exception cref="ArgumentException">A required value is null, a URI is relative or the interval is reversed or empty.</exception>
    public EvidenceRecord(
        EvidenceReference reference,
        Uri providerIdentifier,
        Uri? sourceGraph = null,
        string? sourceRecordIdentifier = null,
        string? snapshotIdentifier = null,
        DateTimeOffset? observedAt = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null)
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        ArgumentNullException.ThrowIfNull(providerIdentifier);
        if (!providerIdentifier.IsAbsoluteUri)
        {
            throw new ArgumentException("A provider identifier must be an absolute URI.", nameof(providerIdentifier));
        }

        if (sourceGraph is not null && !sourceGraph.IsAbsoluteUri)
        {
            throw new ArgumentException("A source graph identifier must be an absolute URI.", nameof(sourceGraph));
        }
        if (validFrom >= validTo)
        {
            throw new ArgumentException("An evidence validity interval start must precede its end.", nameof(validTo));
        }

        ProviderIdentifier = providerIdentifier;
        SourceGraph = sourceGraph;
        SourceRecordIdentifier = sourceRecordIdentifier;
        SnapshotIdentifier = snapshotIdentifier;
        ObservedAt = observedAt;
        ValidFrom = validFrom;
        ValidTo = validTo;
    }

    /// <summary>Gets the reference by which assertions identify this evidence.</summary>
    public EvidenceReference Reference
    {
        get;
    }

    /// <summary>Gets the absolute provider identifier.</summary>
    public Uri ProviderIdentifier
    {
        get;
    }

    /// <summary>Gets the source graph URI, or null when unspecified.</summary>
    public Uri? SourceGraph
    {
        get;
    }

    /// <summary>Gets the provider's source record identifier, or null when unavailable.</summary>
    public string? SourceRecordIdentifier
    {
        get;
    }

    /// <summary>Gets the source snapshot identifier, or null when unavailable.</summary>
    public string? SnapshotIdentifier
    {
        get;
    }

    /// <summary>Gets the observation time, or null when unknown.</summary>
    public DateTimeOffset? ObservedAt
    {
        get;
    }

    /// <summary>Gets the reported validity start, or null when unspecified.</summary>
    public DateTimeOffset? ValidFrom
    {
        get;
    }

    /// <summary>Gets the reported validity end, or null when unspecified.</summary>
    public DateTimeOffset? ValidTo
    {
        get;
    }
}

/// <summary>Classifies the severity of a diagnostic accompanying a query result.</summary>
public enum QueryDiagnosticSeverity
{
    /// <summary>Additional information about execution or interpretation.</summary>
    Information,

    /// <summary>A condition callers should consider when interpreting the result.</summary>
    Warning,

    /// <summary>An error reported by the query producer.</summary>
    Error
}

/// <summary>A producer-supplied diagnostic optionally associated with a particular evidence record.</summary>
/// <param name="Code">The machine-readable diagnostic identifier.</param>
/// <param name="Message">The human-readable explanation.</param>
/// <param name="Severity">The diagnostic severity.</param>
/// <param name="Evidence">An associated evidence reference, or null when the diagnostic is not evidence-specific.</param>
public sealed record QueryResultDiagnostic(
    string Code,
    string Message,
    QueryDiagnosticSeverity Severity,
    EvidenceReference? Evidence = null);

/// <summary>Combines a typed query result with correlation, evidence, semantic models and diagnostics.</summary>
public sealed class QueryResultEnvelope
{
    private readonly IReadOnlyDictionary<EvidenceReference, EvidenceRecord> _evidence;

    /// <summary>Captures result metadata, indexing evidence by reference and deduplicating semantic model references.</summary>
    /// <param name="queryId">The query identity supplied by the producer.</param>
    /// <param name="result">The nonnull typed result; retained by reference.</param>
    /// <param name="correlationId">An optional caller or execution correlation identity.</param>
    /// <param name="evidence">Optional evidence records with unique references; copied into an index.</param>
    /// <param name="semanticModels">Optional semantic model references; copied and deduplicated.</param>
    /// <param name="diagnostics">Optional diagnostics; copied in input order.</param>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    /// <exception cref="ArgumentException">Evidence records repeat the same reference.</exception>
    public QueryResultEnvelope(
        Guid queryId,
        QueryResult result,
        Guid? correlationId = null,
        IEnumerable<EvidenceRecord>? evidence = null,
        IEnumerable<SemanticModelReference>? semanticModels = null,
        IEnumerable<QueryResultDiagnostic>? diagnostics = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        QueryId = queryId;
        CorrelationId = correlationId;
        _evidence = (evidence ?? Array.Empty<EvidenceRecord>())
            .ToDictionary(record => record.Reference);
        SemanticModels = Array.AsReadOnly(
            (semanticModels ?? Array.Empty<SemanticModelReference>()).Distinct().ToArray());
        Diagnostics = Array.AsReadOnly(
            (diagnostics ?? Array.Empty<QueryResultDiagnostic>()).ToArray());
    }

    /// <summary>Gets the query identity supplied by the producer.</summary>
    public Guid QueryId
    {
        get;
    }

    /// <summary>Gets the optional correlation identity.</summary>
    public Guid? CorrelationId
    {
        get;
    }

    /// <summary>Gets the typed result associated with this metadata.</summary>
    public QueryResult Result
    {
        get;
    }

    /// <summary>Gets a newly allocated collection of the indexed evidence records.</summary>
    public IReadOnlyCollection<EvidenceRecord> Evidence => _evidence.Values.ToArray();

    /// <summary>Gets the captured distinct semantic model references.</summary>
    public IReadOnlyList<SemanticModelReference> SemanticModels
    {
        get;
    }

    /// <summary>Gets the captured diagnostics in input order.</summary>
    public IReadOnlyList<QueryResultDiagnostic> Diagnostics
    {
        get;
    }

    /// <summary>Resolves a value's evidence reference within this result envelope.</summary>
    /// <param name="reference">Evidence identity carried by a result value.</param>
    /// <param name="record">Receives the corresponding evidence record when available.</param>
    /// <returns>True when the envelope contains the referenced evidence.</returns>
    public bool TryGetEvidence(EvidenceReference reference, out EvidenceRecord? record) =>
        _evidence.TryGetValue(reference, out record);
}
