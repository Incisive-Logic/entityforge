using EntityForge.Cqrs.Repository.Inbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs
{
    /// <summary>Handles messages dispatched through the CQRS infrastructure.</summary>
    public interface IMessageHandler : IMessageHandlerHostObject
    {
        /// <summary>
        /// The method that handles the message.
        /// </summary>
        /// <param name="message">The message being handled.</param>
        /// <param name="unitOfWork">A unit of work that provides access to any data required in the handling.</param>
        Task HandleAsync(IMessage message, IInboxUnitOfWork unitOfWork);
    }

    /// <summary>Handles messages dispatched through the CQRS infrastructure.</summary>
    public interface IMessageHandler<T> : IMessageHandlerHostObject
        where T : IMessage
    {
        /// <summary>
        /// The method that handles the message.
        /// </summary>
        /// <param name="message">The message being handled.</param>
        /// <param name="unitOfWork">A unit of work that provides access to any data required in the handling.</param>
        Task HandleAsync(T message, IInboxUnitOfWork unitOfWork);
    }
}
