using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.Serialization
{
    /// <summary>Serializes events using the gRPC transport envelope.</summary>
    public interface IGrpcEventSerializer : IEventSerializer
    {
    }
}
