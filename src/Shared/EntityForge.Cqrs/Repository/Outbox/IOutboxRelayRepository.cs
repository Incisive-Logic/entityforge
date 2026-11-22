namespace EntityForge.Cqrs.Repository.Outbox;

/// <summary>
/// Provides ordered relay access to committed outbox records.
/// </summary>
public interface IOutboxRelayRepository
{
    /// <summary>Reads persisted outbox events in ascending append-index order.</summary>
    Task<IReadOnlyList<OutboxEventAggregate>> GetAllEventsInAscendingIndexOrderAsync(CancellationToken token);

    /// <summary>Removes outbox events after successful relay.</summary>
    Task RemoveOutboxEventsAsync(
        IReadOnlyList<OutboxEventAggregate> eventAggregates,
        CancellationToken token = default);
}
