namespace EntityForge.Db.Rdf;

/// <summary>Bounds snapshot retention for paged RDF reads.</summary>
/// <remarks>These settings apply to externally resumable quad-page continuations. They do not set SPARQL result limits or hosted streaming-scan budgets. Retained readers can delay reclamation of old state; callers should finish or explicitly release them.</remarks>
public sealed class RdfQueryPagingOptions
{
    /// <summary>Maximum simultaneous retained continuations per database, from 1 to 4,096. Defaults to 128.</summary>
    public int MaximumActiveContinuations { get; init; } = 128;

    /// <summary>Idle time before a continuation expires, from one millisecond to one hour. Defaults to five minutes.</summary>
    /// <remarks>Each successful page read renews the idle expiry. Expired tokens cannot resume the read; physical cleanup may occur later when the database prunes retained continuations.</remarks>
    public TimeSpan ContinuationIdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    internal void Validate()
    {
        if (MaximumActiveContinuations is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumActiveContinuations));
        }
        if (ContinuationIdleTimeout < TimeSpan.FromMilliseconds(1) || ContinuationIdleTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ContinuationIdleTimeout));
        }
        ArgumentNullException.ThrowIfNull(Clock);
    }
}

/// <summary>Indicates that the database has reached its configured retained-query capacity.</summary>
/// <param name="message">The capacity condition preventing admission.</param>
public sealed class RdfQueryCapacityException(string message) : EntityForgeDbException(message);
