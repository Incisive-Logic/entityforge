using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Cqrs.Events.Serialization
{
    /// <summary>Encodes and decodes event messages for journal or transport storage.</summary>
    public interface IEventSerializer
    {
        /// <summary>Serializes an event message into its transport representation.</summary>
        byte[] Serialize<TEvent>(TEvent eventObject)
            where TEvent : IEventMessage;

        /// <summary>Deserializes an event payload into its declared event type.</summary>
        TEvent Deserialize<TEvent>(byte[] bytes)
            where TEvent : IEventMessage;

        /// <summary>Deserializes an event payload into its declared event type.</summary>
        IEventMessage Deserialize(byte[] bytes, Type type);
    }
}
