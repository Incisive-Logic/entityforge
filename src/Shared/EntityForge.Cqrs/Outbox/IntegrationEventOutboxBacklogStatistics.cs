namespace EntityForge.Cqrs.Outbox;

/// <summary>Reports pending outbox volume and the age boundary of the backlog.</summary>
/// <param name="PendingCount">The number of pending outbox records.</param>
/// <param name="OldestCreatedAtUtc">The creation time of the oldest pending record, or null for an empty backlog.</param>
public sealed record IntegrationEventOutboxBacklogStatistics(
    long PendingCount,
    DateTime? OldestCreatedAtUtc);
