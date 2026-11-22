using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events
{
    /// <summary>
    /// A message that represents an event, i.e. something that has happened
    /// in the past. Events should be expressed in the past tense.
    /// </summary>
    public interface IEventMessage : IMessage
    {
        /// <summary>
        /// The current schema version of the event. This is specified so that, should it be necessary
        /// to process an event of an earlier schema version, the current software build can apply
        /// any necessary transformation to it in order to process it with a newer software build
        /// </summary>
        int SchemaVersion
        {
            get;
        }

        /// <summary>
        /// The date/time on which the event occurred
        /// </summary>
        DateTime CreationTime
        {
            get;
        }
    }
}
