using EntityForge.Cqrs.Events;

namespace EntityForge.Cqrs.Domain;

/// <summary>
/// Publishes domain events to in-process handlers after their unit of work has committed.
/// Domain events published here never cross a process boundary.
/// </summary>
public interface ICommittedDomainEventDispatcher
{
    /// <summary>Registers a handler for committed domain events of the selected type.</summary>
    void Register<TDomainEvent>(IDomainEventHandler<TDomainEvent> handler)
        where TDomainEvent : IDomainEvent;

    /// <summary>Removes a previously registered committed-domain-event handler.</summary>
    void Unregister<TDomainEvent>(IDomainEventHandler<TDomainEvent> handler)
        where TDomainEvent : IDomainEvent;

    /// <summary>Dispatches events after persistence has committed; handler failure cannot roll back the durable commit.</summary>
    Task DispatchAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken token = default);
}
