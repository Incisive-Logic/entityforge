using EntityForge.Cqrs.Repository.Inbox;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events
{
    /// <summary>
    /// Implemented by objects that handle particular events
    /// </summary>
    public interface IEventHandler<TEvent> : IEventHandlerHostObject where TEvent : IEventMessage
    {
        /// <summary>Processes an event using the supplied event-handling context.</summary>
        Task HandleAsync(TEvent @event, IInboxUnitOfWork unitOfWork);
    }

    /// <summary>
    /// Implemented by objects that handle any event type on a topic
    /// </summary>
    public interface IEventHandler : IEventHandlerHostObject
    {
        /// <summary>Processes an event using the supplied event-handling context.</summary>
        Task HandleAsync(IEventMessage @event, IInboxUnitOfWork unitOfWork);
    }
}
