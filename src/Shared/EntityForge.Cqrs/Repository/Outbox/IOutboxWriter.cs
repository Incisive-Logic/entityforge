namespace EntityForge.Cqrs.Repository.Outbox;

/// <summary>
/// Stages integration events in the transaction owned by the current unit of work.
/// </summary>
public interface IOutboxWriter
{
    /// <summary>Appends events to the end of the persistence outbox.</summary>
    Task AppendEventsToEndOfOutboxAsync(
        IReadOnlyList<OutboxEventAggregate> eventAggregates,
        CancellationToken token = default);
}
