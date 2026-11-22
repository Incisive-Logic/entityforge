using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.EventStreaming
{
    /// <summary>
    /// Standard message for acknowledging a request with optional error message
    /// </summary>
    public class AcknowledgementResponse
    {
        /// <summary>Gets or sets the error returned when an acknowledgement fails.</summary>
        public string Error
        {
            get; set;
        }
    }
}
