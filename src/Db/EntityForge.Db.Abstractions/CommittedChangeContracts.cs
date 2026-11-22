namespace EntityForge.Db;

/// <summary>Distinguishes newly applied native effects, recognized retries and authoritative commit decisions.</summary>
public enum DatabaseCommitOutcome : byte
{
    /// <summary>The transaction committed and changed native state.</summary>
    AppliedWithChanges = 1,

    /// <summary>The transaction committed without changing native state.</summary>
    AppliedNoChange = 2,

    /// <summary>The transaction identity was already committed and its retained outcome was replayed.</summary>
    IdempotentReplay = 3,

    /// <summary>An authoritative commit is known; a native effect count is not part of this receipt.</summary>
    Committed = 4
}

/// <summary>An opaque committed-change position. Preserve its exact value when resuming a feed.</summary>
public readonly record struct DatabaseChangeCursor
{
    /// <summary>Wraps a nonblank cursor without interpreting its contents.</summary>
    /// <param name="value">The cursor issued by the database or change-feed API.</param>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">The value is empty or whitespace.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public DatabaseChangeCursor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>Gets the opaque cursor text. Do not infer ordering or construct a position from its spelling.</summary>
    public string Value
    {
        get;
    }

    /// <summary>Returns the underlying identifier without adding formatting or changing its case.</summary>
    /// <returns>The identifier value.</returns>
    public override string ToString() => Value;
}

/// <summary>Identifies a committed operation and its version, profile and available native-effect metadata.</summary>
public sealed record DatabaseCommitReceipt
{
    /// <summary>Creates a receipt with a consistent native or authority-only outcome.</summary>
    /// <param name="databaseIncarnation">The nonempty identity of the destination database incarnation.</param>
    /// <param name="transactionId">The nonempty identity of the committed transaction.</param>
    /// <param name="profile">The database profile that interpreted the transaction.</param>
    /// <param name="version">The authoritative committed version.</param>
    /// <param name="outcome">The native outcome or authority-only committed decision.</param>
    /// <param name="changeCursor">The native change-feed cursor when available; must be null for an authority-only receipt.</param>
    /// <param name="effectCount">A nonnegative native effect count, or null for an authority-only receipt.</param>
    /// <exception cref="ArgumentException">An identity is empty or the native metadata does not match the outcome category.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A profile or outcome is undefined, or the effect count is negative.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public DatabaseCommitReceipt(Guid databaseIncarnation, Guid transactionId, DatabaseProfileId profile,
        DatabaseVersion version, DatabaseCommitOutcome outcome, DatabaseChangeCursor? changeCursor,
        int? effectCount)
    {
        if (databaseIncarnation == Guid.Empty)
        {
            throw new ArgumentException("A database incarnation is required.", nameof(databaseIncarnation));
        }

        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("A transaction identity is required.", nameof(transactionId));
        }

        if (!Enum.IsDefined(profile))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (effectCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectCount));
        }

        if (outcome == DatabaseCommitOutcome.Committed ? effectCount is not null || changeCursor is not null : effectCount is null)
        {
            throw new ArgumentException("A native receipt requires an effect count; an authority receipt has no native count or cursor.");
        }

        DatabaseIncarnation = databaseIncarnation;
        TransactionId = transactionId;
        Profile = profile;
        Version = version;
        Outcome = outcome;
        ChangeCursor = changeCursor;
        EffectCount = effectCount;
    }

    /// <summary>Gets the destination incarnation, preventing confusion between independent or recreated databases.</summary>
    public Guid DatabaseIncarnation
    {
        get;
    }

    /// <summary>Gets the committed transaction identity used to correlate retries.</summary>
    public Guid TransactionId
    {
        get;
    }

    /// <summary>Gets the profile that interpreted the transaction.</summary>
    public DatabaseProfileId Profile
    {
        get;
    }

    /// <summary>Gets the committed version represented by this receipt.</summary>
    public DatabaseVersion Version
    {
        get;
    }

    /// <summary>Gets whether this was a new application, no-op, replay or authority-only commit.</summary>
    public DatabaseCommitOutcome Outcome
    {
        get;
    }

    /// <summary>Gets the native change-feed cursor when available; null does not mean the transaction failed.</summary>
    public DatabaseChangeCursor? ChangeCursor
    {
        get;
    }

    /// <summary>Native effects, or null when the receipt describes an authoritative distributed decision.</summary>
    public int? EffectCount
    {
        get;
    }
}

/// <summary>A case-sensitive kind/value pair used to conservatively filter committed changes.</summary>
public sealed record CommittedChangeRoutingKey
{
    /// <summary>Maximum number of characters in a routing-kind name.</summary>
    public const int MaximumKindCharacters = 64;

    /// <summary>Maximum number of characters in a routing value.</summary>
    public const int MaximumValueCharacters = 4_096;

    /// <summary>Creates a bounded routing key without normalizing its kind or value.</summary>
    /// <param name="kind">The routing dimension, such as a profile-defined property or entity category.</param>
    /// <param name="value">The exact value within that dimension.</param>
    /// <exception cref="ArgumentNullException">The kind or value is null.</exception>
    /// <exception cref="ArgumentException">The kind or value is empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A string exceeds its character limit.</exception>
    public CommittedChangeRoutingKey(string kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (kind.Length > MaximumKindCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (value.Length > MaximumValueCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Kind = kind;
        Value = value;
    }

    /// <summary>Gets the routing dimension.</summary>
    public string Kind
    {
        get;
    }

    /// <summary>Gets the exact routing value within its dimension.</summary>
    public string Value
    {
        get;
    }
}

/// <summary>A bounded set of exact routing keys plus overflow markers that prevent false-negative filtering.</summary>
public sealed record CommittedChangeRoutingSummary
{
    /// <summary>Maximum exact keys retained across all routing dimensions.</summary>
    public const int MaximumRoutingKeys = 16_384;

    /// <summary>Maximum exact keys retained per dimension by <see cref="CreateBounded"/>.</summary>
    public const int MaximumRoutingKeysPerKind = 4_096;

    /// <summary>Maximum distinct overflow dimensions represented by a summary.</summary>
    public const int MaximumOverflowedKinds = 16_384;

    /// <summary>Gets a complete summary containing no routing keys.</summary>
    public static CommittedChangeRoutingSummary Empty { get; } = new();

    /// <summary>Captures and deduplicates exact keys and overflow markers within the total limits.</summary>
    /// <param name="keys">Exact routing keys, or null for none.</param>
    /// <param name="overflowedKinds">Kinds whose exact values were omitted; null means no overflow.</param>
    /// <exception cref="ArgumentException">An overflow kind is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A total limit or kind-length limit is exceeded.</exception>
    public CommittedChangeRoutingSummary(
        IReadOnlyList<CommittedChangeRoutingKey>? keys = null,
        IReadOnlyList<string>? overflowedKinds = null)
    {
        CommittedChangeRoutingKey[] materializedKeys = keys?.Distinct().ToArray() ?? [];
        string[] materializedOverflowedKinds = overflowedKinds?
            .Select(ValidateKind).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (materializedKeys.Length > MaximumRoutingKeys)
        {
            throw new ArgumentOutOfRangeException(nameof(keys));
        }

        if (materializedOverflowedKinds.Length > MaximumOverflowedKinds)
        {
            throw new ArgumentOutOfRangeException(nameof(overflowedKinds));
        }

        Keys = materializedKeys;
        OverflowedKinds = materializedOverflowedKinds;
    }

    /// <summary>Gets the captured exact routing keys.</summary>
    public IReadOnlyList<CommittedChangeRoutingKey> Keys
    {
        get;
    }

    /// <summary>Gets kinds for which any requested value must be treated as a possible match.</summary>
    public IReadOnlyList<string> OverflowedKinds
    {
        get;
    }

    /// <summary>Gets whether every routing dimension is represented without an overflow marker.</summary>
    public bool IsComplete => OverflowedKinds.Count == 0;

    /// <summary>Tests whether a routing summary can conservatively include a requested key.</summary>
    /// <param name="requested">The kind and value to test against the retained routing keys.</param>
    /// <returns>True for an exact retained key or an overflowed kind; false proves that this summary does not contain the key.</returns>
    /// <remarks>True is a routing hint, not proof of a matching logical change.</remarks>
    public bool MayContain(CommittedChangeRoutingKey requested) =>
        Keys.Contains(requested) || OverflowedKinds.Contains(requested.Kind, StringComparer.Ordinal);

    /// <summary>Builds a deterministic bounded routing summary, marking kinds whose exact keys exceed the limits.</summary>
    /// <param name="keys">Routing keys to deduplicate and group by kind.</param>
    /// <returns>A summary containing retained exact keys and overflow markers.</returns>
    /// <exception cref="ArgumentNullException">The sequence is null.</exception>
    /// <exception cref="ArgumentException">There are too many distinct routing kinds.</exception>
    public static CommittedChangeRoutingSummary CreateBounded(
        IEnumerable<CommittedChangeRoutingKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        CommittedChangeRoutingKey[] distinct = keys.Distinct().ToArray();
        if (distinct.Length <= MaximumRoutingKeys && distinct
                .GroupBy(key => key.Kind, StringComparer.Ordinal)
                .All(group => group.Count() <= MaximumRoutingKeysPerKind))
        {
            return new(distinct);
        }

        IGrouping<string, CommittedChangeRoutingKey>[] groups = distinct
            .GroupBy(key => key.Kind, StringComparer.Ordinal)
            .OrderBy(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        if (groups.Length > MaximumOverflowedKinds)
        {
            throw new ArgumentOutOfRangeException(nameof(keys),
                "The routing summary contains too many distinct routing kinds.");
        }

        var bounded = new List<CommittedChangeRoutingKey>(MaximumRoutingKeys);
        var overflowed = new List<string>();
        foreach (IGrouping<string, CommittedChangeRoutingKey> group in groups)
        {
            CommittedChangeRoutingKey[] groupKeys = group.ToArray();
            if (groupKeys.Length <= MaximumRoutingKeysPerKind &&
                groupKeys.Length <= MaximumRoutingKeys - bounded.Count)
            {
                bounded.AddRange(groupKeys);
            }
            else
            {
                overflowed.Add(group.Key);
            }
        }
        return new(bounded, overflowed);
    }

    private static string ValidateKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (kind.Length > CommittedChangeRoutingKey.MaximumKindCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        return kind;
    }
}

/// <summary>Describes how a committed change can be consumed.</summary>
public enum CommittedChangeKind : byte
{
    /// <summary>The payload describes incremental logical effects.</summary>
    IncrementalEffects = 1,

    /// <summary>Incremental application is insufficient; the consumer must reload the affected dataset.</summary>
    DatasetReset = 2
}

/// <summary>A versioned profile-specific change payload with routing metadata and snapshot-refresh semantics.</summary>
public sealed record CommittedChangePayload
{
    /// <summary>Copies an encoded payload and captures its interpretation metadata.</summary>
    /// <param name="codecId">The nonblank identifier of the profile-specific codec.</param>
    /// <param name="codecVersion">The positive codec version required to interpret the bytes.</param>
    /// <param name="content">The encoded bytes; copied so the caller can reuse the source buffer.</param>
    /// <param name="effectCount">The nonnegative number of logical effects represented.</param>
    /// <param name="routing">Optional conservative routing metadata; null selects an empty summary.</param>
    /// <param name="kind">Whether to apply incremental effects or refresh the dataset snapshot.</param>
    /// <exception cref="ArgumentException">The codec identifier is blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The codec version, effect count or change kind is invalid.</exception>
    public CommittedChangePayload(string codecId, int codecVersion, ReadOnlyMemory<byte> content,
        int effectCount, CommittedChangeRoutingSummary? routing = null,
        CommittedChangeKind kind = CommittedChangeKind.IncrementalEffects)
        : this(codecId, codecVersion, content, effectCount, routing, kind, borrowContent: false)
    {
    }

    private CommittedChangePayload(string codecId, int codecVersion, ReadOnlyMemory<byte> content,
        int effectCount, CommittedChangeRoutingSummary? routing, CommittedChangeKind kind,
        bool borrowContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codecId);
        if (codecVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(codecVersion));
        }

        if (effectCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(effectCount));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        CodecId = codecId;
        CodecVersion = codecVersion;
        Content = borrowContent ? content : content.ToArray();
        EffectCount = effectCount;
        Routing = routing ?? CommittedChangeRoutingSummary.Empty;
        Kind = kind;
    }

    internal static CommittedChangePayload BorrowOwnedContent(string codecId, int codecVersion,
        ReadOnlyMemory<byte> content, int effectCount,
        CommittedChangeRoutingSummary? routing = null,
        CommittedChangeKind kind = CommittedChangeKind.IncrementalEffects) =>
        new(codecId, codecVersion, content, effectCount, routing, kind, borrowContent: true);

    /// <summary>Gets the identifier of the codec required to interpret <see cref="Content"/>.</summary>
    public string CodecId
    {
        get;
    }

    /// <summary>Gets the codec version required to interpret the payload.</summary>
    public int CodecVersion
    {
        get;
    }

    /// <summary>Gets the captured encoded bytes.</summary>
    public ReadOnlyMemory<byte> Content
    {
        get;
    }

    /// <summary>Gets the number of logical effects reported by the producer.</summary>
    public int EffectCount
    {
        get;
    }

    /// <summary>Gets conservative routing hints; an overflow match is not proof that a particular key changed.</summary>
    public CommittedChangeRoutingSummary Routing
    {
        get;
    }

    /// <summary>Gets how consumers must apply the change.</summary>
    public CommittedChangeKind Kind
    {
        get;
    }

    /// <summary>Gets whether the reported logical effect count is nonzero.</summary>
    public bool HasEffects => EffectCount != 0;

    /// <summary>Gets whether consumers must reload a snapshot instead of applying only incremental effects.</summary>
    public bool RequiresSnapshotRefresh => Kind == CommittedChangeKind.DatasetReset;
}

/// <summary>One committed transaction's change-feed entry and its profile-specific payload.</summary>
/// <param name="DatabaseIncarnation">The database incarnation that issued this entry.</param>
/// <param name="Version">The committed database version.</param>
/// <param name="TransactionId">The committed transaction identity.</param>
/// <param name="Profile">The profile that produced the payload.</param>
/// <param name="Cursor">The opaque feed position associated with this entry.</param>
/// <param name="Payload">The encoded effects and routing metadata.</param>
public sealed record CommittedChangeSet(
    Guid DatabaseIncarnation,
    DatabaseVersion Version,
    Guid TransactionId,
    DatabaseProfileId Profile,
    DatabaseChangeCursor Cursor,
    CommittedChangePayload Payload);

/// <summary>Filters a change feed by optional profile and a union of conservative routing keys.</summary>
public sealed record DatabaseChangeSelector
{
    /// <summary>Captures a profile filter and deduplicated routing keys.</summary>
    /// <param name="profile">The required profile, or null for any profile.</param>
    /// <param name="anyRoutingKeys">Keys matched with OR semantics; null or empty leaves routing unrestricted.</param>
    /// <exception cref="ArgumentOutOfRangeException">The profile is undefined or too many distinct keys are supplied.</exception>
    public DatabaseChangeSelector(DatabaseProfileId? profile = null,
        IReadOnlyList<CommittedChangeRoutingKey>? anyRoutingKeys = null)
    {
        if (profile is not null && !Enum.IsDefined(profile.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(profile));
        }

        CommittedChangeRoutingKey[] keys = anyRoutingKeys?.Distinct().ToArray() ?? [];
        if (keys.Length > CommittedChangeRoutingSummary.MaximumRoutingKeys)
        {
            throw new ArgumentOutOfRangeException(nameof(anyRoutingKeys));
        }

        Profile = profile;
        AnyRoutingKeys = keys;
    }

    /// <summary>Gets the required profile, or null for any profile.</summary>
    public DatabaseProfileId? Profile
    {
        get;
    }

    /// <summary>Gets the captured keys; any conservative match may include a change.</summary>
    public IReadOnlyList<CommittedChangeRoutingKey> AnyRoutingKeys
    {
        get;
    }
}

/// <summary>The retained positions reported by a change feed at the time of inspection.</summary>
/// <param name="Earliest">The earliest retained cursor, when one is available.</param>
/// <param name="Current">The current cursor, when one is available.</param>
public sealed record DatabaseChangeFeedBounds(
    DatabaseChangeCursor? Earliest,
    DatabaseChangeCursor? Current);

/// <summary>A bounded page of change-feed entries and its continuation state.</summary>
/// <param name="Changes">The returned entries in feed order. The supplied collection is retained by this record.</param>
/// <param name="NextCursor">The opaque position to use for the next read, when available.</param>
/// <param name="HasMore">Whether another page was available when this page was produced; false does not rule out future commits.</param>
public sealed record DatabaseChangePage(
    IReadOnlyList<CommittedChangeSet> Changes,
    DatabaseChangeCursor? NextCursor,
    bool HasMore);

/// <summary>Allocation and paging bounds shared by change-feed clients and producers.</summary>
public static class DatabaseChangeFeedLimits
{
    /// <summary>Maximum number of entries requested in one page.</summary>
    public const int MaximumPageSize = 1_000;

    /// <summary>Default number of entries requested per subscription page.</summary>
    public const int DefaultSubscriptionPageSize = 100;

    /// <summary>Maximum encoded committed-change payload size in bytes.</summary>
    public const int MaximumCommittedChangePayloadBytes = 256 * 1024 * 1024;
}

/// <summary>Configures bounded page acquisition for a change-feed subscription.</summary>
public sealed record DatabaseChangeSubscriptionOptions
{
    /// <summary>Creates subscription options with a positive page size within the feed limit.</summary>
    /// <param name="pageSize">The maximum entries requested per page.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page size is zero, negative or above the maximum.</exception>
    public DatabaseChangeSubscriptionOptions(int pageSize = DatabaseChangeFeedLimits.DefaultSubscriptionPageSize)
    {
        if (pageSize <= 0 || pageSize > DatabaseChangeFeedLimits.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        PageSize = pageSize;
    }

    /// <summary>Gets the maximum requested page size.</summary>
    public int PageSize
    {
        get;
    }
}

/// <summary>Indicates that a resume position predates retained change history and cannot be replayed.</summary>
public sealed class EntityForgeChangeCursorExpiredException : EntityForgeDbException
{
    /// <summary>Reports the expired cursor and the earliest position still available.</summary>
    /// <param name="requested">The unavailable resume cursor.</param>
    /// <param name="earliest">The earliest retained cursor, when available.</param>
    public EntityForgeChangeCursorExpiredException(DatabaseChangeCursor requested,
        DatabaseChangeCursor? earliest)
        : base($"Change cursor '{requested}' is older than retained change history.")
    {
        Requested = requested;
        Earliest = earliest;
    }

    /// <summary>Gets the expired resume cursor.</summary>
    public DatabaseChangeCursor Requested
    {
        get;
    }

    /// <summary>Gets the earliest retained cursor, when available. Recovery may require a new snapshot before resubscribing.</summary>
    public DatabaseChangeCursor? Earliest
    {
        get;
    }
}

/// <summary>Indicates that a cursor cannot be interpreted for the requested feed.</summary>
/// <param name="message">The validation diagnostic.</param>
public sealed class EntityForgeChangeCursorInvalidException(string message)
    : EntityForgeDbException(message);
