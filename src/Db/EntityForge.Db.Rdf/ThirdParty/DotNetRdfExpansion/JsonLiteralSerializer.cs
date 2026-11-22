/*
// <copyright>
// dotNetRDF is free and open source software licensed under the MIT License
// -------------------------------------------------------------------------
// 
// Copyright (c) 2009-2026 dotNetRDF Project (http://dotnetrdf.org/)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is furnished
// to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// </copyright>
*/

using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#nullable disable
namespace EntityForge.Db.Rdf.JsonLdExpansion;

/// <summary>
/// Overrides some of the default Newtonsoft.Json JSON value formatting so that
/// the output of the JSON-LD writer is better conforming to the JSON-LD 1.1 specification.
/// </summary>
internal class JsonLiteralSerializer
{
    /// <summary>
    /// Return a string serialization of the provided token.
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public string Serialize(JToken token)
    {
        var sb = new StringBuilder();
        var sw = new StringWriter(sb);
        using (var writer = new JsonTextWriter(sw))
        {
            writer.Formatting = Formatting.None;
            Serialize(writer, token);
        }

        return sb.ToString();
    }

    private static void Serialize(JsonWriter writer, JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
                writer.WriteStartObject();
                foreach (JProperty property in (token as JObject).Properties().OrderBy(p=>p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Serialize(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JTokenType.Array:
                writer.WriteStartArray();
                foreach (JToken item in (token as JArray))
                {
                    Serialize(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JTokenType.Float:
            case JTokenType.Integer:
                var number = token.Type == JTokenType.Float ? token.Value<double>() :
                    double.Parse(token.ToString(Formatting.None), System.Globalization.CultureInfo.InvariantCulture);
                writer.WriteRawValue(RdfJsonNumber.Format(number));
                break;
            default:
                writer.WriteRawValue(token.ToString(Formatting.None));
                break;
        }
    }
}


