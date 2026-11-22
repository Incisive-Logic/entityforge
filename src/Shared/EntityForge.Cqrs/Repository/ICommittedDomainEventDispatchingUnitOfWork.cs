using EntityForge.Cqrs.Domain;

namespace EntityForge.Cqrs.Repository;

/// <summary>
/// Exposes post-commit, process-local domain event dispatch separately from persistence.
/// </summary>
public interface ICommittedDomainEventDispatchingUnitOfWork
{
    /// <summary>Dispatches events only after the owning unit of work has committed successfully.</summary>
    Task DispatchCommittedDomainEventsAsync(
        IReadOnlyCollection<IDomainEvent> events,
        CancellationToken token);
}
