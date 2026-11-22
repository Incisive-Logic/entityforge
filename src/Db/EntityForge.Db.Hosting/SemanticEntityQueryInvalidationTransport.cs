using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting;

/// <summary>Requests a resumable check for changes that may alter a prior entity-query result.</summary>
internal sealed record SemanticEntityQueryInvalidationRequest(
    PortableEntityQueryRequest Query,
    long ObservedSnapshotVersion,
    string? Cursor = null,
    int MaximumChanges = DatabaseChangeFeedLimits.DefaultSubscriptionPageSize);

/// <summary>Starts a resumable server stream of changes relevant to a prior query result.</summary>
internal sealed record SemanticEntityQuerySubscriptionRequest(
    PortableEntityQueryRequest Query,
    long ObservedSnapshotVersion,
    string? Cursor = null,
    int PageSize = DatabaseChangeFeedLimits.DefaultSubscriptionPageSize,
    int HeartbeatSeconds = SemanticEntityQuerySubscriptionLimits.DefaultHeartbeatSeconds);

internal static class SemanticEntityQuerySubscriptionLimits
{
    public const int DefaultHeartbeatSeconds = 15;
    public const int MinimumHeartbeatSeconds = 1;
    public const int MaximumHeartbeatSeconds = 60;
}

/// <summary>Portable representation of a transport-neutral query-subscription event.</summary>
internal sealed record SemanticEntityQuerySubscriptionEventResponse(
    string Kind,
    long ObservedSnapshotVersion,
    long ChangeVersion,
    Guid TransactionId,
    string Cursor,
    IReadOnlyList<string> RelevantEffects);

/// <summary>Reports whether a prior entity-query result should be refreshed.</summary>
internal sealed record SemanticEntityQueryInvalidationResponse(
    bool IsInvalidated,
    long ObservedSnapshotVersion,
    long? InvalidatingVersion,
    Guid? TransactionId,
    string? NextCursor,
    bool HasMore,
    int ScannedChanges,
    IReadOnlyList<string> RelevantEffects);

internal static class SemanticEntityQueryInvalidationResponseMapper
{
    public static SemanticEntityQueryInvalidationResponse Map(SemanticEntityQueryInvalidation value) => new(
        value.IsInvalidated, value.ObservedSnapshotVersion.Value, value.InvalidatingVersion?.Value,
        value.TransactionId, value.NextCursor?.Value, value.HasMore, value.ScannedChanges,
        value.RelevantEffects.Select(item => item.ToString()).ToArray());
}

internal static class SemanticEntityQuerySubscriptionEventResponseMapper
{
    public static SemanticEntityQuerySubscriptionEventResponse Map(SemanticEntityQuerySubscriptionEvent value) =>
        new(value.Kind.ToString().ToLowerInvariant(), value.ObservedSnapshotVersion.Value,
            value.ChangeVersion.Value, value.TransactionId, value.Cursor.Value,
            value.RelevantEffects.Select(item => item.ToString()).ToArray());
}
