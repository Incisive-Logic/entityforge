using Grpc.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace EntityForge.Cqrs
{
    /// <summary>A message envelope could not be serialized or deserialized.</summary>
    public class SerializedMessageException : Exception
    {
        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public SerializedMessageException(string message) : base(message)
        {
        }

        /// <summary>Creates an exception describing the failure and any supplied underlying cause.</summary>
        public SerializedMessageException(string message, Exception ex) : base(message, ex)
        {
        }
    }

    /// <summary>
    /// Generic interface for a serialized message
    /// Includes a type description so that the receiver knowns how to deserialize it
    /// </summary>
    public class SerializedMessage
    {
        /// <summary>Encodes the supplied message with the configured message serializer.</summary>
        public static string Serialize(object messageObject)
        {
            return JsonSerializer.Serialize(Create(messageObject));
        }

        /// <summary>Creates a transport envelope for a message and its serialized representation.</summary>
        public static SerializedMessage Create(object messageObject)
        {
            return new SerializedMessage()
            {
                Type = messageObject.GetType().AssemblyQualifiedName,
                SerializedData = JsonSerializer.Serialize(messageObject)
            };
        }

        /// <summary>Decodes the envelope into its declared message type.</summary>
        public static object Deserialize(string serializedMessage)
        {
            SerializedMessage message = JsonSerializer.Deserialize<SerializedMessage>(serializedMessage);
            System.Type messageType = System.Type.GetType(message.Type);

            if (messageType == null)
            {
                throw new SerializedMessageException($"Could not find type {message.Type} - check that the libary holding this type has been loaded by the application");
            }

            return JsonSerializer.Deserialize(message.SerializedData, messageType);
        }

        /// <summary>
        /// A type description capable of telling the receiver the object type
        /// and how to deserialize it
        /// </summary>
        public string Type
        {
            get; set;
        }

        /// <summary>
        /// The serialized data, held as a string
        /// </summary>
        public string SerializedData
        {
            get; set;
        }
    }
}
