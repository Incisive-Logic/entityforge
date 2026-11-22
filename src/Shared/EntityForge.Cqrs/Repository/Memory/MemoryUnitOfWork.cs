using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using EntityForge.Cqrs.Domain;
using EntityForge.Cqrs.Repository.Memory;
using EntityForge.Utility;

namespace EntityForge.Cqrs.Repository.Memory
{
    /// <summary>
    /// Base class for any unit of work that stores DDD entities in
    /// memory based respositories
    /// </summary>
    public abstract class MemoryUnitOfWork :
        IUnitOfWork,
        ICommittedDomainEventDispatchingUnitOfWork
    {
        private readonly MemoryDataStore _memoryDataStore;
        private readonly ICommittedDomainEventDispatcher _committedDomainEventDispatcher;
        private readonly IUnitOfWorkSession _session;
        private int _commitState;

        /// <summary>Creates a unit of work over the supplied in-memory store and context.</summary>
        protected MemoryUnitOfWork(
            MemoryDataStore memoryDataStore,
            ICommittedDomainEventDispatcher committedDomainEventDispatcher = null)
        {
            ArgumentNullException.ThrowIfNull(memoryDataStore, nameof(memoryDataStore));
            _memoryDataStore = memoryDataStore;
            _committedDomainEventDispatcher = committedDomainEventDispatcher;

            _session = memoryDataStore.BeginUnitOfWork(this);
            RegisterMemoryRepositories(_session);
        }

        /// <summary>Releases the session's tracked entities and owned resources.</summary>
        public virtual ValueTask DisposeAsync()
        {
            _memoryDataStore.EndUnitOfWork(this);
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Instantiate your in memory repository implementations here
        /// </summary>
        protected abstract void RegisterMemoryRepositories(IUnitOfWorkSession unitOfWorkSession);

        /// <inheritdoc />
        public virtual Task RollbackAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _commitState) == 2)
            {
                throw new InvalidOperationException("A committed unit of work cannot be reverted.");
            }

            _memoryDataStore.RevertChanges(this);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyCollection<IDomainEvent>> CommitAsync(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _commitState, 1, 0) != 0)
            {
                throw new InvalidOperationException("A unit of work may be committed successfully only once.");
            }

            IReadOnlyCollection<IDomainEvent> events;
            try
            {
                events = await CommitChangesAsync(token).ConfigureAwait(false);
                Volatile.Write(ref _commitState, 2);
            }
            catch
            {
                Volatile.Write(ref _commitState, 0);
                throw;
            }

            return events;
        }

        /// <inheritdoc />
        public Task DispatchCommittedDomainEventsAsync(
            IReadOnlyCollection<IDomainEvent> events,
            CancellationToken token) =>
            _committedDomainEventDispatcher == null || events.Count == 0
                ? Task.CompletedTask
                : _committedDomainEventDispatcher.DispatchAsync(events, token);

        /// <summary>Publishes tracked entity changes to the in-memory store and returns committed domain events.</summary>
        protected virtual Task<IReadOnlyCollection<IDomainEvent>> CommitChangesAsync(
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(_memoryDataStore.SaveChanges(this));
        }

        /// <summary>Gets the entity tracking session owned by this unit of work.</summary>
        protected IUnitOfWorkSession Session => _session;

        /// <summary>Gets the context associated with this unit of work.</summary>
        protected MemoryDataStore Context => _memoryDataStore;
    }
}
