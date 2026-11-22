using EntityForge.Cqrs.Events;

namespace EntityForge.Cqrs.Outbox;

/// <summary>
/// Persists addressed integration events before journal publication is attempted.
/// </summary>
public interface IIntegrationEventOutboxWriterStore
{
    /// <summary>Appends integration-event records to the outbox in persistence order.</summary>
    Task AppendAsync(
        string destinationTopic,
        IReadOnlyList<IIntegrationEvent> events,
        CancellationToken token);
}
