using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Deserializes an abstract or interface JSON contract using a specified concrete CLR type.</summary>
    public class ConcreteConverter<T> : JsonConverter
    {
        /// <summary>Reports whether this converter accepts the requested CLR type.</summary>
        public override bool CanConvert(Type objectType) => true;

        /// <summary>Deserializes JSON into the configured concrete type.</summary>
        public override object ReadJson(JsonReader reader,
         Type objectType, object existingValue, JsonSerializer serializer)
        {
            return serializer.Deserialize<T>(reader);
        }

        /// <summary>Writes the supplied value through the configured serializer.</summary>
        public override void WriteJson(JsonWriter writer,
            object value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, value);
        }
    }
}
