using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EntityForge.Cqrs.Events;

namespace EntityForge.Cqrs.Repository.Outbox
{
    /// <summary>
    /// A unit of work that can store integration events in an outbox
    /// atomically with any changes to other repositories in the unit of work
    ///
    /// An outbox commits events atomically to the data storage at the same time as any
    /// changes to the domain they represent, so preventing failures causing inconsistency
    /// between consumers acting on the changes and the domain model
    ///
    /// See https://docs.microsoft.com/en-us/azure/architecture/best-practices/transactional-outbox-cosmos
    ///
    /// A relay can retry publishing those events, removing them only after successful delivery.
    /// Local domain events are dispatched in process after commit and do not belong here.
    /// </summary>
    public interface IOutboxingUnitOfWork : IUnitOfWork
    {
        /// <summary>
        /// The repository containing the outbox events.
        /// </summary>
        IOutboxRepository OutboxRepository
        {
            get;
        }

        /// <summary>Gets the writer that persists integration events with the unit of work.</summary>
        IOutboxWriter OutboxWriter => OutboxRepository;

        /// <summary>Gets the repository used to enumerate and acknowledge persisted outbox events.</summary>
        IOutboxRelayRepository OutboxRelayRepository => OutboxRepository;
    }
}
