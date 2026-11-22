using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Repository.Outbox
{
    /// <summary>
    /// A repository for integration events awaiting publication.
    /// Events are explicitly staged atomically with domain changes.
    /// </summary>
    public interface IOutboxRepository : IOutboxWriter, IOutboxRelayRepository
    {
    }
}
