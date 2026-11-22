using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs
{
    /// <summary>Creates an independent message copy for dispatch or retention.</summary>
    public interface ICloneableMessage<T>
    {
        /// <summary>Returns a clone of this message using the message's declared clone semantics.</summary>
        T CreateClone();
    }
}
