using EntityForge.Cqrs.Repository.Memory;
using EntityForge.Utility;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Repository.Outbox
{
    /// <summary>
    /// A memory based outbox repository. Given this is not crash/restart proof as it is memory persistance,
    /// this should NOT be used in production circumstances but only for testing.
    /// </summary>
    public class MemoryOutboxRepository : IOutboxRepository, IComparer<OutboxEventAggregate>
    {
        private readonly IUnitOfWorkSession _session;

        /// <summary>Creates an outbox repository bound to an in-memory unit of work.</summary>
        public MemoryOutboxRepository(IUnitOfWorkSession session)
        {
            ArgumentNullException.ThrowIfNull(session, nameof(session));
            _session = session;
        }

        /// <summary>Orders outbox records by their append index.</summary>
        public int Compare(OutboxEventAggregate x, OutboxEventAggregate y)
        {
            int orderComparison = x.OrderIndex.CompareTo(y.OrderIndex);
            return orderComparison != 0 ? orderComparison : x.Id.CompareTo(y.Id);
        }

        /// <inheritdoc />
        public Task RemoveOutboxEventsAsync(
            IReadOnlyList<OutboxEventAggregate> eventAggregates,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (eventAggregates == null)
            {
                return Task.CompletedTask;
            }

            foreach (OutboxEventAggregate eventAggregate in eventAggregates)
            {
                // Remove the event from the entity store
                _session.RemoveEntityFromStore<OutboxEventAggregate, Guid>(nameof(OutboxEventAggregate), eventAggregate);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AppendEventsToEndOfOutboxAsync(
            IReadOnlyList<OutboxEventAggregate> eventAggregates,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (eventAggregates == null)
            {
                return Task.CompletedTask;
            }

            int eventCount = _session.GetEntities<OutboxEventAggregate, Guid>(nameof(OutboxEventAggregate)).Count();

            foreach (OutboxEventAggregate eventAggregate in eventAggregates)
            {
                // Set the order index (zero based)
                eventAggregate.SetOrderIndex(eventCount);

                // Add the event to the entity store (this will allocate an ID)
                _session.AddEntityToStore<OutboxEventAggregate, Guid>(nameof(OutboxEventAggregate), eventAggregate);

                // Increment the event count for the next event
                eventCount++;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<OutboxEventAggregate>> GetAllEventsInAscendingIndexOrderAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            // Convert ImmutableList to IReadOnlyList using .ToList()
            var events = _session.GetEntities<OutboxEventAggregate, Guid>(nameof(OutboxEventAggregate), this).ToList();
            return Task.FromResult((IReadOnlyList<OutboxEventAggregate>)events);
        }
    }
}
