using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs
{
    /// <summary>Marks a message accepted by the CQRS messaging infrastructure.</summary>
    public interface IMessage
    {
        /// <summary>
        /// The unique ID of the message
        /// In event streaming, this can be the 'key' of the message and can also be used as a correlation ID
        /// </summary>
        Guid Id
        {
            get;
        }

        /// <summary>
        /// An optional OpenTelemetry Activity ID
        /// </summary>
        string TelemetryActivityId
        {
            get; set;
        }
    }
}
