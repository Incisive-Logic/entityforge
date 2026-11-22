using System;
using EntityForge.Cqrs.Domain;
using EntityForge.Cqrs.Repository.Memory;

namespace EntityForge.Cqrs.Repository.Inbox
{
    internal sealed class InboxMessageAggregate : AggregateRoot<string>, ICloneable
    {
        public InboxMessageAggregate(string id, DateTime processedAt)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            Id = id;
            ProcessedAt = processedAt;
        }

        private InboxMessageAggregate(InboxMessageAggregate copy)
        {
            Id = copy.Id;
            ProcessedAt = copy.ProcessedAt;
        }

        public DateTime ProcessedAt
        {
            get;
        }

        public object Clone() => new InboxMessageAggregate(this);
    }

    /// <summary>
    /// A memory inbox backed by the owning unit-of-work session. This keeps the
    /// processed marker atomic with business and outbox changes in the same store.
    /// </summary>
    public sealed class MemoryInboxRepository : IInboxRepository
    {
        /// <summary>The in-memory entity store name used for inbox records.</summary>
        public const string StoreName = nameof(InboxMessageAggregate);
        private readonly IUnitOfWorkSession _session;

        /// <summary>Creates an inbox repository bound to an in-memory unit-of-work session.</summary>
        public MemoryInboxRepository(IUnitOfWorkSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        /// <inheritdoc />
        public Task<bool> TryAddProcessedMessageAsync(
            string id,
            DateTime processedAt,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_session.GetEntityByID<InboxMessageAggregate, string>(StoreName, id) != null)
            {
                return Task.FromResult(false);
            }

            _session.AddEntityToStore<InboxMessageAggregate, string>(
                StoreName,
                new InboxMessageAggregate(id, processedAt));
            return Task.FromResult(true);
        }
    }
}
