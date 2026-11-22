using EntityForge.Cqrs.Domain;

namespace EntityForge.Cqrs.Repository.Outbox;

/// <summary>
/// Maps local domain events to explicitly addressed integration events that must
/// be committed to an outbox with the domain changes.
/// </summary>
public interface IIntegrationEventOutboxMapper
{
    /// <summary>Maps committed domain events into integration-event outbox records.</summary>
    IReadOnlyCollection<OutboxEventAggregate> Map(
        IReadOnlyCollection<IDomainEvent> domainEvents);
}
