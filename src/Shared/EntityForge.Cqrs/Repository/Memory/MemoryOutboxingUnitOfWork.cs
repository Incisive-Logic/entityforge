using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using EntityForge.Cqrs.Domain;
using EntityForge.Cqrs.Repository.Outbox;

namespace EntityForge.Cqrs.Repository.Memory
{
    /// <summary>
    /// A memory unit of work that stores outbox events transactionally
    ///
    /// Outbox events must be stored transactionally with changes persisted in
    /// a domain store to ensure both are enacted atomically i.e. if one fails,
    /// the other also fails
    /// </summary>
    public abstract class MemoryOutboxingUnitOfWork : MemoryUnitOfWork, IOutboxingUnitOfWork
    {
        private IOutboxRepository _outboxRepository;
        private readonly IIntegrationEventOutboxMapper _integrationEventOutboxMapper;

        /// <summary>Creates an in-memory unit of work that persists integration events with entity changes.</summary>
        protected MemoryOutboxingUnitOfWork(
            MemoryDataStore memoryDataStore,
            ICommittedDomainEventDispatcher committedDomainEventDispatcher = null,
            IIntegrationEventOutboxMapper integrationEventOutboxMapper = null)
            : base(memoryDataStore, committedDomainEventDispatcher)
        {
            _integrationEventOutboxMapper = integrationEventOutboxMapper;
            memoryDataStore.RegisterEntityStore<Guid>(nameof(OutboxEventAggregate));
        }

        /// <summary>Commits tracked entity changes together with their outbox records.</summary>
        protected override async Task<IReadOnlyCollection<IDomainEvent>> CommitChangesAsync(
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyCollection<IDomainEvent> domainEvents = Context.GetPendingDomainEvents(this);
            IReadOnlyCollection<OutboxEventAggregate> integrationEvents =
                _integrationEventOutboxMapper?.Map(domainEvents) ?? [];

            if (integrationEvents.Count > 0)
            {
                await OutboxRepository
                    .AppendEventsToEndOfOutboxAsync(integrationEvents.ToArray(), token)
                    .ConfigureAwait(false);
            }

            return await base.CommitChangesAsync(token).ConfigureAwait(false);
        }

        /// <inheritdoc />
        protected override void RegisterMemoryRepositories(IUnitOfWorkSession unitOfWorkSession)
        {
            _outboxRepository = new MemoryOutboxRepository(unitOfWorkSession);
        }

        /// <inheritdoc />
        public IOutboxRepository OutboxRepository => _outboxRepository;
    }
}
