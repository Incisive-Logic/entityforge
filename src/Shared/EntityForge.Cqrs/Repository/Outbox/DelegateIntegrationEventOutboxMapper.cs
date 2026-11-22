using EntityForge.Cqrs.Domain;

namespace EntityForge.Cqrs.Repository.Outbox;

/// <summary>Uses a caller-supplied function to map domain events into integration-event outbox records.</summary>
public sealed class DelegateIntegrationEventOutboxMapper : IIntegrationEventOutboxMapper
{
    private readonly Func<IDomainEvent, OutboxEventAggregate> _mapper;

    /// <summary>Creates an outbox mapper from the supplied mapping delegate.</summary>
    public DelegateIntegrationEventOutboxMapper(
        Func<IDomainEvent, OutboxEventAggregate> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        _mapper = mapper;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<OutboxEventAggregate> Map(
        IReadOnlyCollection<IDomainEvent> domainEvents)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);
        return domainEvents
            .Select(_mapper)
            .Where(entry => entry != null)
            .ToArray();
    }
}
