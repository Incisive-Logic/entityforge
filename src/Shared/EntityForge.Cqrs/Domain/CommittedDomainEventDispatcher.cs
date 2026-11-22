using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using EntityForge.Cqrs.Events;

namespace EntityForge.Cqrs.Domain;

/// <summary>
/// Dispatches committed domain events to handlers in the current process.
/// Handler failures are logged because persistence has already committed and cannot be rolled back.
/// </summary>
public sealed class CommittedDomainEventDispatcher : ICommittedDomainEventDispatcher
{
    private interface IRegistration
    {
        object Handler
        {
            get;
        }

        Task HandleAsync(IDomainEvent domainEvent);
    }

    private sealed class Registration<TDomainEvent> : IRegistration
        where TDomainEvent : IDomainEvent
    {
        private readonly IDomainEventHandler<TDomainEvent> _handler;

        public Registration(IDomainEventHandler<TDomainEvent> handler) => _handler = handler;

        public object Handler => _handler;

        public Task HandleAsync(IDomainEvent domainEvent) =>
            _handler.HandleAsync((TDomainEvent)domainEvent, null);
    }

    private readonly ConcurrentDictionary<Type, ImmutableArray<IRegistration>> _handlers = new();
    private readonly ILogger _logger;

    /// <summary>Creates an in-process committed-event dispatcher with optional failure logging.</summary>
    public CommittedDomainEventDispatcher(ILogger logger = null) =>
        _logger = logger ?? NullLogger.Instance;

    /// <inheritdoc />
    public void Register<TDomainEvent>(IDomainEventHandler<TDomainEvent> handler)
        where TDomainEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.AddOrUpdate(
            typeof(TDomainEvent),
            _ => [new Registration<TDomainEvent>(handler)],
            (_, registrations) => registrations.Any(x => ReferenceEquals(x.Handler, handler))
                ? registrations
                : registrations.Add(new Registration<TDomainEvent>(handler)));
    }

    /// <inheritdoc />
    public void Unregister<TDomainEvent>(IDomainEventHandler<TDomainEvent> handler)
        where TDomainEvent : IDomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers.AddOrUpdate(
            typeof(TDomainEvent),
            _ => [],
            (_, registrations) => registrations.RemoveAll(x => ReferenceEquals(x.Handler, handler)));
    }

    /// <inheritdoc />
    public async Task DispatchAsync(
        IReadOnlyCollection<IDomainEvent> domainEvents,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvents);

        foreach (IDomainEvent domainEvent in domainEvents)
        {
            token.ThrowIfCancellationRequested();
            IRegistration[] registrations = _handlers
                .Where(pair => pair.Key.IsAssignableFrom(domainEvent.GetType()))
                .SelectMany(pair => pair.Value)
                .ToArray();

            foreach (IRegistration registration in registrations)
            {
                try
                {
                    await registration.HandleAsync(domainEvent).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Committed domain event handler {HandlerType} failed for {DomainEventType}.",
                        registration.Handler.GetType().Name,
                        domainEvent.GetType().Name);
                }
            }
        }
    }
}
