using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using EntityForge.Cqrs.Events;

namespace EntityForge.Cqrs.Domain
{
    /// <summary>Marks an event describing a domain state change.</summary>
    public interface IDomainEvent : IEventMessage
    {
    }
}
