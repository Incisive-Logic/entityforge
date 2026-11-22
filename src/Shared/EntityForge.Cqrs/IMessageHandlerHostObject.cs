using EntityForge.Cqrs.Repository.Inbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs
{
    /// <summary>
    /// Interface for objects that host message handlers.
    /// </summary>
    public interface IMessageHandlerHostObject
    {
        /// <summary>Creates a unit of work for handling a message.</summary>
        ValueTask<IInboxUnitOfWork> CreateUnitOfWorkAsync(
            Type eventType,
            CancellationToken token);

        /// <summary>Gets the stable handler identity used to associate processing state.</summary>
        Guid HandlerID
        {
            get;
        }

        /// <summary>
        /// Gets the stable logical identity used for durable inbox deduplication.
        /// </summary>
        string ConsumerID => HandlerID.ToString("D");

        /// <summary>Gets a human-readable description of the handler.</summary>
        string HandlerDescription
        {
            get;
        }
    }
}
