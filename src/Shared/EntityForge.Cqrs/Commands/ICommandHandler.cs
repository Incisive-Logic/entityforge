using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using EntityForge.Cqrs.Queue;

namespace EntityForge.Cqrs.Commands
{
    /// <summary>
    /// A message handler that handles command messages
    /// </summary>
    public interface ICommandHandler<TCommandMessage> : IMessageHandler<TCommandMessage>
        where TCommandMessage : ICommandMessage
    {
    }
}
