using Google.Protobuf;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.IO;

namespace EntityForge.Cqrs.Events.Serialization
{
    /// <summary>Converts event messages to and from the gRPC event envelope.</summary>
    public class GrpcEventSerializer : IGrpcEventSerializer
    {
        private static IEventMessage ParseEvent(Type eventType, byte[] bytes)
        {
            var propertyInfo = eventType.GetProperty("Parser");
            var parser = propertyInfo.GetValue(null);
            return (IEventMessage)parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) }).Invoke(parser, new object[] { bytes });
        }

        /// <inheritdoc />
        public TEvent Deserialize<TEvent>(byte[] bytes) where TEvent : IEventMessage
        {
            return (TEvent)ParseEvent(typeof(TEvent), bytes);
        }

        /// <inheritdoc />
        public IEventMessage Deserialize(byte[] bytes, Type type)
        {
            return ParseEvent(type, bytes);
        }

        /// <inheritdoc />
        public byte[] Serialize<TEvent>(TEvent eventObject) where TEvent : IEventMessage
        {
            using (var stream = new MemoryStream())
            {
                // In gRPC case, the event object is a protobuf message
                MessageExtensions.WriteTo((Google.Protobuf.IMessage)eventObject, stream);
                return stream.ToArray();
            }
        }
    }
}
