namespace EntityForge.Semantics;

/// <summary>Declares whether a session carries incremental changes or a complete source-partition snapshot.</summary>
public enum SemanticChangeSessionKind
{
    /// <summary>Applies the supplied changes without declaring the source contribution complete.</summary>
    Incremental,

    /// <summary>Declares a complete snapshot for the session's source partition.</summary>
    SourceSnapshot
}

/// <summary>Identifies one opaque source position. Entity Forge never compares position text for ordering.</summary>
public sealed record SemanticSourcePosition
{
    /// <summary>Creates an opaque source position scoped to a nonempty generation and version.</summary>
    /// <param name="generationId">The nonempty source generation identity.</param>
    /// <param name="sourceVersion">The nonblank source version identifier.</param>
    /// <param name="sourcePosition">The nonblank opaque position text; no ordering is inferred from it.</param>
    /// <exception cref="ArgumentException">The generation is empty or a string is null or blank.</exception>
    public SemanticSourcePosition(Guid generationId, string sourceVersion, string sourcePosition)
    {
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException("A source generation identifier cannot be empty.", nameof(generationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePosition);
        GenerationId = generationId;
        SourceVersion = sourceVersion;
        SourcePosition = sourcePosition;
    }

    /// <summary>Gets the source generation identity.</summary>
    public Guid GenerationId
    {
        get;
    }

    /// <summary>Gets the source version identifier.</summary>
    public string SourceVersion
    {
        get;
    }

    /// <summary>Gets the exact opaque source position text.</summary>
    public string SourcePosition
    {
        get;
    }
}

/// <summary>Specifies the source progress that must exist before a session advances it.</summary>
public enum SemanticSourceProgressExpectation
{
    /// <summary>Requires that no prior progress has been established for the source partition.</summary>
    NoPriorProgress,

    /// <summary>Requires an exact match with a supplied predecessor position.</summary>
    ExactPredecessor
}

/// <summary>Declares the source-partition frontier that must exist immediately before a session commits.</summary>
public sealed record SemanticSourceProgressPrecondition
{
    /// <summary>Defines an initial or exact-predecessor source-progress guard.</summary>
    /// <param name="expectation">The required progress state.</param>
    /// <param name="predecessor">Required only for ExactPredecessor; otherwise must be null.</param>
    /// <exception cref="ArgumentOutOfRangeException">The expectation is undefined.</exception>
    /// <exception cref="ArgumentException">Predecessor presence disagrees with the expectation.</exception>
    public SemanticSourceProgressPrecondition(
        SemanticSourceProgressExpectation expectation,
        SemanticSourcePosition? predecessor = null)
    {
        if (!Enum.IsDefined(expectation))
        {
            throw new ArgumentOutOfRangeException(nameof(expectation));
        }

        if ((expectation is SemanticSourceProgressExpectation.ExactPredecessor) != (predecessor is not null))
        {
            throw new ArgumentException(
                "An exact source-progress precondition requires one predecessor; an initial precondition cannot have one.",
                nameof(predecessor));
        }

        Expectation = expectation;
        Predecessor = predecessor;
    }

    /// <summary>Gets the guard requiring an initial source partition with no prior progress.</summary>
    public static SemanticSourceProgressPrecondition Initial
    {
        get;
    }
    =
        new(SemanticSourceProgressExpectation.NoPriorProgress);

    /// <summary>Requires the selected source contribution to follow a specific predecessor position.</summary>
    /// <param name="predecessor">The prior source position expected by the submitting application.</param>
    /// <returns>A predecessor-bound source-progress precondition.</returns>
    public static SemanticSourceProgressPrecondition After(SemanticSourcePosition predecessor) =>
        new(SemanticSourceProgressExpectation.ExactPredecessor,
            predecessor ?? throw new ArgumentNullException(nameof(predecessor)));

    /// <summary>Gets the required progress state.</summary>
    public SemanticSourceProgressExpectation Expectation
    {
        get;
    }

    /// <summary>Gets the required predecessor, or null for the initial-state guard.</summary>
    public SemanticSourcePosition? Predecessor
    {
        get;
    }
}

/// <summary>Identifies a source partition and the opaque position represented by a semantic change session.</summary>
public sealed record SemanticChangeSessionSource
{
    /// <summary>Captures source identity and position without comparing position text or contacting the source.</summary>
    /// <param name="sourceSystem">The nonnull absolute source-system URI.</param>
    /// <param name="sourcePartition">The nonnull absolute source-partition URI.</param>
    /// <param name="sourceVersion">The nonblank source version identifier.</param>
    /// <param name="generationId">The nonempty source generation identity.</param>
    /// <param name="sourcePosition">The nonblank opaque source position.</param>
    /// <exception cref="ArgumentException">An identity, URI or required string is invalid.</exception>
    public SemanticChangeSessionSource(
        Uri sourceSystem,
        Uri sourcePartition,
        string sourceVersion,
        Guid generationId,
        string sourcePosition)
    {
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(sourcePartition);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceVersion);
        if (!sourceSystem.IsAbsoluteUri)
        {
            throw new ArgumentException("A source-system identifier must be absolute.", nameof(sourceSystem));
        }

        if (!sourcePartition.IsAbsoluteUri)
        {
            throw new ArgumentException("A source-partition identifier must be absolute.", nameof(sourcePartition));
        }

        if (generationId == Guid.Empty)
        {
            throw new ArgumentException("A source generation identifier cannot be empty.", nameof(generationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePosition);

        SourceSystem = sourceSystem;
        SourcePartition = sourcePartition;
        SourceVersion = sourceVersion;
        GenerationId = generationId;
        SourcePosition = sourcePosition;
    }

    /// <summary>Gets the absolute source-system identifier.</summary>
    public Uri SourceSystem
    {
        get;
    }

    /// <summary>Gets the absolute source-partition identifier.</summary>
    public Uri SourcePartition
    {
        get;
    }

    /// <summary>Gets the source version identifier.</summary>
    public string SourceVersion
    {
        get;
    }

    /// <summary>Gets the source generation identity.</summary>
    public Guid GenerationId
    {
        get;
    }

    /// <summary>Gets the opaque source position text, which has no database-defined ordering.</summary>
    public string SourcePosition
    {
        get;
    }

    /// <summary>Gets the current opaque position independently of its partition identity.</summary>
    public SemanticSourcePosition Position => new(GenerationId, SourceVersion, SourcePosition);
}

/// <summary>Declares the identity, source ownership and completeness interpretation of a semantic change session.</summary>
public sealed record SemanticChangeSessionStart
{
    /// <summary>Creates session metadata without staging or committing any changes.</summary>
    /// <param name="sessionId">The nonempty stable session identity.</param>
    /// <param name="kind">Whether the session is incremental or a complete source snapshot.</param>
    /// <param name="source">The nonnull source partition and represented position.</param>
    /// <param name="producerActivityId">The nonempty producer activity identity.</param>
    /// <param name="startedAt">The producer-reported session start time.</param>
    /// <param name="correlationId">An optional nonblank correlation identifier.</param>
    /// <param name="progressPrecondition">An optional atomic frontier guard; null permits an unguarded transition.</param>
    /// <exception cref="ArgumentException">An identity is empty, the source is null or a supplied correlation identifier is blank.</exception>
    public SemanticChangeSessionStart(
        Guid sessionId,
        SemanticChangeSessionKind kind,
        SemanticChangeSessionSource source,
        Guid producerActivityId,
        DateTimeOffset startedAt,
        string? correlationId = null,
        SemanticSourceProgressPrecondition? progressPrecondition = null)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A semantic session identifier cannot be empty.", nameof(sessionId));
        }

        if (producerActivityId == Guid.Empty)
        {
            throw new ArgumentException("A producer activity identifier cannot be empty.", nameof(producerActivityId));
        }

        if (correlationId is not null && string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("A correlation identifier cannot be empty.", nameof(correlationId));
        }

        SessionId = sessionId;
        Kind = kind;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        ProducerActivityId = producerActivityId;
        StartedAt = startedAt;
        CorrelationId = correlationId;
        ProgressPrecondition = progressPrecondition;
    }

    /// <summary>Gets the stable session identity.</summary>
    public Guid SessionId
    {
        get;
    }

    /// <summary>Gets the session's completeness interpretation.</summary>
    public SemanticChangeSessionKind Kind
    {
        get;
    }

    /// <summary>Gets the source partition and represented position.</summary>
    public SemanticChangeSessionSource Source
    {
        get;
    }

    /// <summary>Gets the producer activity identity.</summary>
    public Guid ProducerActivityId
    {
        get;
    }

    /// <summary>Gets the producer-reported session start time.</summary>
    public DateTimeOffset StartedAt
    {
        get;
    }

    /// <summary>Gets the optional correlation identifier.</summary>
    public string? CorrelationId
    {
        get;
    }

    /// <summary>
    /// Gets an optional atomic source-frontier guard. Null deliberately means that the producer accepts an
    /// unguarded last-writer transition.
    /// </summary>
    public SemanticSourceProgressPrecondition? ProgressPrecondition
    {
        get;
    }
}

/// <summary>A numbered nonempty batch of uniquely identified changes belonging to one session.</summary>
public sealed record SemanticChangeSessionPage
{
    /// <summary>Captures a page's ordered changes and checks identity uniqueness within that page.</summary>
    /// <param name="sessionId">The nonempty session identity.</param>
    /// <param name="pageNumber">The nonnegative zero-based page number.</param>
    /// <param name="changes">A nonempty sequence of nonnull changes with distinct change identities; copied.</param>
    /// <exception cref="ArgumentException">The identity or changes are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page number is negative.</exception>
    public SemanticChangeSessionPage(
        Guid sessionId,
        int pageNumber,
        IEnumerable<SemanticEntityChange> changes)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A semantic session identifier cannot be empty.", nameof(sessionId));
        }

        if (pageNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        ArgumentNullException.ThrowIfNull(changes);
        var materialized = changes.ToArray();
        if (materialized.Length == 0 || materialized.Any(change => change is null))
        {
            throw new ArgumentException("A semantic session page requires non-null changes.", nameof(changes));
        }

        if (materialized.Select(change => change.ChangeId).Distinct().Count() != materialized.Length)
        {
            throw new ArgumentException("A semantic session page cannot contain duplicate change identifiers.", nameof(changes));
        }

        SessionId = sessionId;
        PageNumber = pageNumber;
        Changes = Array.AsReadOnly(materialized);
    }

    /// <summary>Gets the owning session identity.</summary>
    public Guid SessionId
    {
        get;
    }

    /// <summary>Gets the zero-based page number.</summary>
    public int PageNumber
    {
        get;
    }

    /// <summary>Gets the captured changes in input order.</summary>
    public IReadOnlyList<SemanticEntityChange> Changes
    {
        get;
    }
}

/// <summary>Declares the expected session size and digests used to verify a complete submitted session.</summary>
public sealed record SemanticChangeSessionClose
{
    /// <summary>Creates a completion declaration without verifying page content or committing the session.</summary>
    /// <param name="sessionId">The nonempty session identity.</param>
    /// <param name="pageCount">The nonnegative expected page count.</param>
    /// <param name="changeCount">The nonnegative expected change count; zero if and only if pageCount is zero.</param>
    /// <param name="contentHash">The page-content digest as sha256: followed by 64 lowercase hexadecimal digits.</param>
    /// <param name="closedAt">The producer-reported close time.</param>
    /// <param name="operationHash">Optional digest of the start and ordered pages in the same format; preferred for replay identity.</param>
    /// <exception cref="ArgumentException">The session identity, count consistency or digest syntax is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    public SemanticChangeSessionClose(
        Guid sessionId,
        int pageCount,
        int changeCount,
        string contentHash,
        DateTimeOffset closedAt,
        string? operationHash = null)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A semantic session identifier cannot be empty.", nameof(sessionId));
        }

        if (pageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }

        if (changeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(changeCount));
        }

        if ((pageCount == 0) != (changeCount == 0))
        {
            throw new ArgumentException("Only an empty session can have zero pages or zero changes.");
        }

        ValidateHash(contentHash, nameof(contentHash));
        SessionId = sessionId;
        PageCount = pageCount;
        ChangeCount = changeCount;
        ContentHash = contentHash;
        ClosedAt = closedAt;
        if (operationHash is not null)
        {
            ValidateHash(operationHash, nameof(operationHash));
        }

        OperationHash = operationHash;
    }

    /// <summary>Gets the session identity.</summary>
    public Guid SessionId
    {
        get;
    }

    /// <summary>Gets the expected total page count.</summary>
    public int PageCount
    {
        get;
    }

    /// <summary>Gets the expected total change count.</summary>
    public int ChangeCount
    {
        get;
    }

    /// <summary>Gets the declared page-content digest; construction checks syntax, not content.</summary>
    public string ContentHash
    {
        get;
    }

    /// <summary>Gets the producer-reported close time.</summary>
    public DateTimeOffset ClosedAt
    {
        get;
    }

    /// <summary>
    /// Gets the optional digest of both the session start and its ordered pages. New producers should supply this
    /// so a retry cannot reinterpret identical payload pages under different source or completeness metadata.
    /// </summary>
    public string? OperationHash
    {
        get;
    }

    /// <summary>Gets the strongest digest available for replay identity.</summary>
    public string ReplayHash => OperationHash ?? ContentHash;

    private static void ValidateHash(string contentHash, string parameterName)
    {
        const string sha256Prefix = "sha256:";
        string digest = contentHash.StartsWith(sha256Prefix, StringComparison.Ordinal)
            ? contentHash[sha256Prefix.Length..]
            : string.Empty;
        if (digest.Length != 64 || digest.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("A semantic session hash must be a lowercase 'sha256:' digest.",
                parameterName);
        }
    }
}

/// <summary>Describes a producer's aborted session and optional failing page; it is not a database commit receipt.</summary>
public sealed record SemanticChangeSessionAbort
{
    /// <summary>Creates an abort description without changing database state.</summary>
    /// <param name="sessionId">The nonempty session identity.</param>
    /// <param name="errorCode">A nonblank machine-readable error code.</param>
    /// <param name="error">A nonblank human-readable explanation.</param>
    /// <param name="abortedAt">The reported abort time.</param>
    /// <param name="failedPageNumber">The nonnegative zero-based failing page, or null when not page-specific.</param>
    /// <exception cref="ArgumentException">The session identity or required error text is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The failing page number is negative.</exception>
    public SemanticChangeSessionAbort(
        Guid sessionId,
        string errorCode,
        string error,
        DateTimeOffset abortedAt,
        int? failedPageNumber = null)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A semantic session identifier cannot be empty.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        if (failedPageNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failedPageNumber));
        }

        SessionId = sessionId;
        ErrorCode = errorCode;
        Error = error;
        AbortedAt = abortedAt;
        FailedPageNumber = failedPageNumber;
    }

    /// <summary>Gets the aborted session identity.</summary>
    public Guid SessionId
    {
        get;
    }

    /// <summary>Gets the machine-readable error code.</summary>
    public string ErrorCode
    {
        get;
    }

    /// <summary>Gets the human-readable explanation.</summary>
    public string Error
    {
        get;
    }

    /// <summary>Gets the reported abort time.</summary>
    public DateTimeOffset AbortedAt
    {
        get;
    }

    /// <summary>Gets the zero-based failing page, or null when not page-specific.</summary>
    public int? FailedPageNumber
    {
        get;
    }
}
