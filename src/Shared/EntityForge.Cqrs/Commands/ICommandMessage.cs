using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Commands
{
    /// <summary>
    /// An immutable command message
    /// </summary>
    public interface ICommandMessage : IMessage
    {
    }
}
