using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events
{
    /// <summary>
    /// Interface for objects that host event handlers.
    /// </summary>
    public interface IEventHandlerHostObject : IMessageHandlerHostObject
    {
    }
}
