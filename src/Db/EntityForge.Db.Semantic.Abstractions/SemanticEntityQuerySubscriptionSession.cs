using EntityForge.Query.Querying;

namespace EntityForge.Db.Semantic;

/// <summary>Bounds the in-memory event queue for a multiplexed query-subscription session.</summary>
public static class SemanticEntityQuerySubscriptionSessionLimits
{
    /// <summary>The default buffered event capacity.</summary>
    public const int DefaultEventCapacity = 64;

    /// <summary>The largest permitted buffered event capacity.</summary>
    public const int MaximumEventCapacity = 1_024;
}

/// <summary>Configures the bounded output buffer shared by a subscription session.</summary>
public sealed record SemanticEntityQuerySubscriptionSessionOptions
{
    /// <summary>Creates options with a positive event capacity within the supported maximum.</summary>
    /// <param name="eventCapacity">Maximum buffered events before producers wait for consumers.</param>
    /// <exception cref="ArgumentOutOfRangeException">Capacity is not positive or exceeds the maximum.</exception>
    public SemanticEntityQuerySubscriptionSessionOptions(
        int eventCapacity = SemanticEntityQuerySubscriptionSessionLimits.DefaultEventCapacity)
    {
        if (eventCapacity <= 0 || eventCapacity > SemanticEntityQuerySubscriptionSessionLimits.MaximumEventCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(eventCapacity));
        }

        EventCapacity = eventCapacity;
    }

    /// <summary>Gets the maximum number of buffered session events.</summary>
    public int EventCapacity
    {
        get;
    }
}

/// <summary>A client command in a transport-neutral multiplexed query-subscription session.</summary>
/// <param name="SubscriptionId">The subscription identity within the session; the session requires a nonempty identity.</param>
public abstract record SemanticEntityQuerySubscriptionCommand(Guid SubscriptionId);

/// <summary>Requests a new query-invalidation subscription in the current session.</summary>
/// <param name="SubscriptionId">The new subscription identity.</param>
/// <param name="Query">The query whose observed result should be monitored.</param>
/// <param name="ObservedSnapshotVersion">The snapshot at which the caller observed the result.</param>
/// <param name="Cursor">An optional retained change position from which to resume.</param>
/// <param name="PageSize">The requested change-feed scan page size, subject to feed limits.</param>
public sealed record StartSemanticEntityQuerySubscription(
    Guid SubscriptionId,
    EntityQueryDefinition Query,
    DatabaseVersion ObservedSnapshotVersion,
    DatabaseChangeCursor? Cursor = null,
    int PageSize = DatabaseChangeFeedLimits.DefaultSubscriptionPageSize)
    : SemanticEntityQuerySubscriptionCommand(SubscriptionId);

/// <summary>Requests replacement of a session subscription with a new query and observation context.</summary>
/// <param name="SubscriptionId">The subscription identity to replace.</param>
/// <param name="Query">The replacement query.</param>
/// <param name="ObservedSnapshotVersion">The snapshot of the replacement observed result.</param>
/// <param name="Cursor">An optional retained change position from which to resume.</param>
/// <param name="PageSize">The requested change-feed scan page size.</param>
public sealed record ReplaceSemanticEntityQuerySubscription(
    Guid SubscriptionId,
    EntityQueryDefinition Query,
    DatabaseVersion ObservedSnapshotVersion,
    DatabaseChangeCursor? Cursor = null,
    int PageSize = DatabaseChangeFeedLimits.DefaultSubscriptionPageSize)
    : SemanticEntityQuerySubscriptionCommand(SubscriptionId);

/// <summary>Updates an existing subscription's observed snapshot while retaining its query and scan page size.</summary>
/// <param name="SubscriptionId">The existing subscription identity.</param>
/// <param name="ObservedSnapshotVersion">The snapshot after the caller refreshes its result.</param>
/// <param name="Cursor">A new resume position, or null to reuse the session's last acknowledged cursor.</param>
public sealed record RefreshSemanticEntityQuerySubscription(
    Guid SubscriptionId,
    DatabaseVersion ObservedSnapshotVersion,
    DatabaseChangeCursor? Cursor = null)
    : SemanticEntityQuerySubscriptionCommand(SubscriptionId);

/// <summary>Records a caller-retained resume cursor in the active subscription session.</summary>
/// <param name="SubscriptionId">The existing subscription identity.</param>
/// <param name="Cursor">The cursor the caller has processed and retained.</param>
/// <remarks>This updates session state; it does not create a durable consumer checkpoint or extend change retention.</remarks>
public sealed record AcknowledgeSemanticEntityQuerySubscription(
    Guid SubscriptionId,
    DatabaseChangeCursor Cursor)
    : SemanticEntityQuerySubscriptionCommand(SubscriptionId);

/// <summary>Requests cancellation of one subscription within the session.</summary>
/// <param name="SubscriptionId">The existing subscription identity to cancel.</param>
public sealed record CancelSemanticEntityQuerySubscription(Guid SubscriptionId)
    : SemanticEntityQuerySubscriptionCommand(SubscriptionId);

/// <summary>Classifies server responses and notifications in a multiplexed subscription session.</summary>
public enum SemanticEntityQuerySubscriptionSessionEventKind : byte
{
    /// <summary>The subscription has started scanning.</summary>
    Started = 1,

    /// <summary>The scan advanced without reporting a relevant result invalidation.</summary>
    Progress = 2,

    /// <summary>A committed change may affect the observed query result.</summary>
    Invalidated = 3,

    /// <summary>The active session accepted a caller's resume cursor.</summary>
    Acknowledged = 4,

    /// <summary>The subscription was cancelled.</summary>
    Cancelled = 5,

    /// <summary>A command or subscription operation was rejected; error fields explain why.</summary>
    Rejected = 6
}

/// <summary>A server event in a transport-neutral multiplexed query-subscription session.</summary>
/// <param name="SubscriptionId">The subscription associated with the event.</param>
/// <param name="Kind">The event category.</param>
/// <param name="ObservedSnapshotVersion">The observed query snapshot, when relevant.</param>
/// <param name="ChangeVersion">The committed change version, when relevant.</param>
/// <param name="TransactionId">The associated committed transaction, when relevant.</param>
/// <param name="Cursor">The associated resume position, when supplied.</param>
/// <param name="RelevantEffects">Effect categories relevant to an invalidation, when supplied.</param>
/// <param name="ErrorCode">The machine-readable rejection code, when supplied.</param>
/// <param name="ErrorMessage">The human-readable rejection explanation, when supplied.</param>
public sealed record SemanticEntityQuerySubscriptionSessionEvent(
    Guid SubscriptionId,
    SemanticEntityQuerySubscriptionSessionEventKind Kind,
    DatabaseVersion? ObservedSnapshotVersion = null,
    DatabaseVersion? ChangeVersion = null,
    Guid? TransactionId = null,
    DatabaseChangeCursor? Cursor = null,
    IReadOnlyList<SemanticCommittedEffectKind>? RelevantEffects = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
