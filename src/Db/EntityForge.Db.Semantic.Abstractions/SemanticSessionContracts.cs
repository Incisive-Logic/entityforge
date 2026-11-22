using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Groups the complete start, ordered pages and close message of one semantic change session.</summary>
public sealed record SemanticSessionEnvelope
{
    /// <summary>Captures the supplied session messages without validating their cross-message hashes or sequence.</summary>
    /// <param name="start">The nonnull session start.</param>
    /// <param name="pages">The nonnull page list, copied in supplied order and containing no null pages.</param>
    /// <param name="close">The nonnull completion declaration.</param>
    /// <exception cref="ArgumentException">An argument or page is null.</exception>
    public SemanticSessionEnvelope(SemanticChangeSessionStart start,
        IReadOnlyList<SemanticChangeSessionPage> pages, SemanticChangeSessionClose close)
    {
        Start = start ?? throw new ArgumentNullException(nameof(start));
        Pages = pages?.Select(page => page ?? throw new ArgumentException(
            "A semantic session page cannot be null.", nameof(pages))).ToArray()
            ?? throw new ArgumentNullException(nameof(pages));
        Close = close ?? throw new ArgumentNullException(nameof(close));
    }

    /// <summary>Gets the retained session start.</summary>
    public SemanticChangeSessionStart Start
    {
        get;
    }

    /// <summary>Gets the captured pages in supplied order.</summary>
    public IReadOnlyList<SemanticChangeSessionPage> Pages
    {
        get;
    }

    /// <summary>Gets the retained completion declaration.</summary>
    public SemanticChangeSessionClose Close
    {
        get;
    }
}

/// <summary>Identifies whether a session was newly applied or recognized as an idempotent replay.</summary>
public enum SessionCommitOutcome
{
    /// <summary>The session was applied natively.</summary>
    Applied,

    /// <summary>The session identity and content were recognized as an already completed operation.</summary>
    IdempotentReplay,

    /// <summary>A commit decision is durable; this outcome alone does not assert native application.</summary>
    Committed
}

/// <summary>Reports the exact opaque source-partition frontier established by a semantic commit.</summary>
public sealed record SemanticSourceCommitProgress
{
    /// <summary>Associates a source-partition position with the database version that established it.</summary>
    /// <param name="sourceSystem">The nonnull absolute source-system identifier.</param>
    /// <param name="sourcePartition">The nonnull absolute source-partition identifier.</param>
    /// <param name="position">The nonnull opaque source position.</param>
    /// <param name="databaseVersion">The database version establishing this source progress.</param>
    /// <exception cref="ArgumentException">A required input is null or a source URI is relative.</exception>
    public SemanticSourceCommitProgress(Uri sourceSystem, Uri sourcePartition,
        SemanticSourcePosition position, DatabaseVersion databaseVersion)
    {
        SourceSystem = sourceSystem ?? throw new ArgumentNullException(nameof(sourceSystem));
        SourcePartition = sourcePartition ?? throw new ArgumentNullException(nameof(sourcePartition));
        if (!sourceSystem.IsAbsoluteUri)
        {
            throw new ArgumentException(
            "A source-system identifier must be absolute.", nameof(sourceSystem));
        }

        if (!sourcePartition.IsAbsoluteUri)
        {
            throw new ArgumentException(
            "A source-partition identifier must be absolute.", nameof(sourcePartition));
        }

        Position = position ?? throw new ArgumentNullException(nameof(position));
        DatabaseVersion = databaseVersion;
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

    /// <summary>Gets the established opaque source position.</summary>
    public SemanticSourcePosition Position
    {
        get;
    }

    /// <summary>Gets the database version that established this source progress.</summary>
    public DatabaseVersion DatabaseVersion
    {
        get;
    }
}

/// <summary>Reports the durable result of committing a semantic session.</summary>
public sealed record SessionCommitResult
{
    /// <summary>Creates a session result and its cross-profile receipt, checking identity and source-progress consistency.</summary>
    /// <param name="sessionId">The nonempty session identity.</param>
    /// <param name="version">The committed database version.</param>
    /// <param name="outcome">The native application, replay or committed-decision outcome.</param>
    /// <param name="databaseIncarnation">The nonempty database incarnation.</param>
    /// <param name="transactionId">The nonempty transaction identity.</param>
    /// <param name="changeCursor">The retained change position, when supplied.</param>
    /// <param name="effectCount">The nonnegative logical effect count, or null when unavailable.</param>
    /// <param name="sourceProgress">The nonnull source progress at exactly the supplied database version.</param>
    /// <exception cref="ArgumentException">An identity or receipt association is invalid, or source progress is null or mismatched.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The outcome is undefined or the effect count is negative.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public SessionCommitResult(Guid sessionId, DatabaseVersion version, SessionCommitOutcome outcome,
        Guid databaseIncarnation, Guid transactionId, DatabaseChangeCursor? changeCursor, int? effectCount,
        SemanticSourceCommitProgress sourceProgress)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A session identity is required.", nameof(sessionId));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (databaseIncarnation == Guid.Empty)
        {
            throw new ArgumentException("A database incarnation is required.", nameof(databaseIncarnation));
        }

        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        if (effectCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectCount));
        }

        SessionId = sessionId;
        Version = version;
        Outcome = outcome;
        DatabaseIncarnation = databaseIncarnation;
        TransactionId = transactionId;
        ChangeCursor = changeCursor;
        EffectCount = effectCount;
        SourceProgress = sourceProgress ?? throw new ArgumentNullException(nameof(sourceProgress));
        if (sourceProgress.DatabaseVersion != version)
        {
            throw new ArgumentException("Source progress must identify the committed database version.",
                nameof(sourceProgress));
        }

        Receipt = new(databaseIncarnation, transactionId, DatabaseProfileId.Semantic, version,
            outcome is SessionCommitOutcome.Committed ? DatabaseCommitOutcome.Committed : outcome is SessionCommitOutcome.IdempotentReplay
                ? DatabaseCommitOutcome.IdempotentReplay
                : effectCount == 0 ? DatabaseCommitOutcome.AppliedNoChange : DatabaseCommitOutcome.AppliedWithChanges,
            changeCursor, effectCount);
    }

    /// <summary>Gets the session identity.</summary>
    public Guid SessionId
    {
        get;
    }

    /// <summary>Gets the committed database version.</summary>
    public DatabaseVersion Version
    {
        get;
    }

    /// <summary>Gets the reported application, replay or commit-decision outcome.</summary>
    public SessionCommitOutcome Outcome
    {
        get;
    }

    /// <summary>Gets the database incarnation issuing the result.</summary>
    public Guid DatabaseIncarnation
    {
        get;
    }

    /// <summary>Gets the committed transaction identity.</summary>
    public Guid TransactionId
    {
        get;
    }

    /// <summary>Gets the retained change position, or null when unavailable.</summary>
    public DatabaseChangeCursor? ChangeCursor
    {
        get;
    }

    /// <summary>Gets the logical effect count, or null when unavailable; zero identifies application without logical changes.</summary>
    public int? EffectCount
    {
        get;
    }

    /// <summary>Gets the source-partition frontier associated with this database version.</summary>
    public SemanticSourceCommitProgress SourceProgress
    {
        get;
    }

    /// <summary>Gets the complete cross-profile receipt, including applied-without-change.</summary>
    public DatabaseCommitReceipt Receipt
    {
        get;
    }
}
