using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Repository.Inbox
{
    /// <summary>
    /// A unit of work that commits a consumer's business changes and inbox confirmation together.
    /// </summary>
    public interface IInboxUnitOfWork : IUnitOfWork
    {
        /// <summary>
        /// The transactional inbox repository for the consuming handler.
        /// </summary>
        IInboxRepository InboxRepository
        {
            get;
        }
    }
}
