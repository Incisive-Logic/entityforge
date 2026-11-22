using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Domain
{
    /// <summary>
    /// An event class representing a change to a domain of some kind
    /// e.g. an update, add or remove of something in the domain model
    /// </summary>
    public abstract class DomainEvent : IDomainEvent
    {
        /// <summary>Creates a domain event carrying its event identity and occurrence metadata.</summary>
        protected DomainEvent(Guid id)
        {
            Id = id;
            CreationTime = DateTime.UtcNow;

            // Set the telemetry information associated with the domain event
            Activity currentActivity = Activity.Current;
            TelemetryActivityId = currentActivity?.Id;
        }

        /// <inheritdoc />
        public Guid Id
        {
            get;
        }

        /// <inheritdoc />
        public DateTime CreationTime
        {
            get;
        }

        /// <inheritdoc />
        public string TelemetryActivityId
        {
            get; set;
        }

        /// <inheritdoc />
        public int SchemaVersion => 0;
    }
}
