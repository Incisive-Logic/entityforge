using System.Diagnostics;
using EntityForge.Cqrs.Domain;

namespace EntityForge.Cqrs.Repository;

/// <summary>Separates a durable unit-of-work outcome from failures during subsequent event dispatch.</summary>
/// <param name="DomainEvents">The domain events produced by the committed unit of work.</param>
/// <param name="PostCommitDispatchException">Any event-dispatch failure after the commit; it does not indicate that persistence rolled back.</param>
public sealed record UnitOfWorkCommitResult(
    IReadOnlyCollection<IDomainEvent> DomainEvents,
    Exception PostCommitDispatchException = null);

/// <summary>
/// Commits persistence first, then performs explicitly post-commit local notification.
/// A notification failure never changes the reported transaction outcome.
/// </summary>
public static class UnitOfWorkCommitCoordinator
{
    /// <summary>Commits the unit of work before dispatching its domain events, preserving the distinction between commit and post-commit failure.</summary>
    public static Task<UnitOfWorkCommitResult> CommitAndDispatchAsync(
        this IUnitOfWork unitOfWork,
        CancellationToken token) => CommitAsync(unitOfWork, token);

    /// <summary>Coordinates the unit-of-work commit and returns its resulting domain events and post-commit outcome.</summary>
    public static async Task<UnitOfWorkCommitResult> CommitAsync(
        IUnitOfWork unitOfWork,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        IReadOnlyCollection<IDomainEvent> events =
            await unitOfWork.CommitAsync(token).ConfigureAwait(false);

        if (unitOfWork is not ICommittedDomainEventDispatchingUnitOfWork dispatcher ||
            events.Count == 0)
        {
            return new UnitOfWorkCommitResult(events);
        }

        try
        {
            await dispatcher
                .DispatchCommittedDomainEventsAsync(events, CancellationToken.None)
                .ConfigureAwait(false);
            return new UnitOfWorkCommitResult(events);
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                $"A unit of work committed successfully, but local domain event dispatch failed: {exception}");
            return new UnitOfWorkCommitResult(events, exception);
        }
    }
}
