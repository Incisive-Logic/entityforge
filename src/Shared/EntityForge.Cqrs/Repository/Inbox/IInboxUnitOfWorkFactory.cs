using EntityForge.Cqrs.Repository.Outbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Repository.Inbox
{
    /// <summary>Creates units of work with inbox support for idempotent message handling.</summary>
    public interface IInboxUnitOfWorkFactory<TInboxUnitOfWork> :
        IUnitOfWorkFactory<TInboxUnitOfWork>
        where TInboxUnitOfWork : IInboxUnitOfWork
    {
    }
}
