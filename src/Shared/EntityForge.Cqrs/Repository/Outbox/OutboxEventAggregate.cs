using EntityForge.Cqrs.Domain;
using EntityForge.Cqrs.Events;
using EntityForge.Cqrs.Exceptions;
using EntityForge.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Repository.Outbox
{
    /// <summary>
    /// The outbox event DDD aggregate root
    /// For the provision of an outbox inline with outbox design pattern, to provide transactional
    /// event handling
    /// </summary>
    public class OutboxEventAggregate : AggregateRoot<Guid>, ICloneable
    {
        /// <summary>Creates an outbox aggregate carrying a persisted event and its ordering metadata.</summary>
        public OutboxEventAggregate(string destinationTopic, IIntegrationEvent eventMessage)
        {
            if (string.IsNullOrWhiteSpace(destinationTopic))
            {
                throw new AggregateValidationException($"{nameof(destinationTopic)} cannot be empty");
            }

            if (eventMessage == null)
            {
                throw new AggregateValidationException($"{nameof(eventMessage)} cannot be null");
            }

            DestinationTopic = destinationTopic;
            EventMessage = eventMessage;
        }

        /// <summary>Creates an outbox aggregate carrying a persisted event and its ordering metadata.</summary>
        public OutboxEventAggregate(OutboxEventAggregate copy)
        {
            ArgumentNullException.ThrowIfNull(copy, nameof(copy));

            Id = copy.Id;
            DestinationTopic = copy.DestinationTopic;
            EventMessage = copy.EventMessage;   // Immutable so doesn't need a clone
            OrderIndex = copy.OrderIndex;
        }

        /// <summary>
        /// Sets the order index of this outbox event, for event sorting purposes
        /// </summary>
        public void SetOrderIndex(long orderIndex)
        {
            if (orderIndex < 0)
            {
                throw new AggregateValidationException($"{orderIndex} cannot be less than zero");
            }

            OrderIndex = orderIndex;
        }

        /// <summary>Creates a copy of the outbox aggregate for unit-of-work tracking.</summary>
        public object Clone()
        {
            return new OutboxEventAggregate(this);
        }

        /// <summary>
        /// The event message to process
        /// This is an immutable value entity
        /// </summary>
        public IIntegrationEvent EventMessage
        {
            get;
        }

        /// <summary>
        /// The event-journal topic to which the relay must publish this event.
        /// </summary>
        public string DestinationTopic
        {
            get;
        }

        /// <summary>
        /// The order index of the event in the outbox
        /// </summary>
        public long OrderIndex
        {
            get; private set;
        }
    }
}
