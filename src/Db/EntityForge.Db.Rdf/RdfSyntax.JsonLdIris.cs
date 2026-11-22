using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VDS.RDF.JsonLd;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSyntax
{
    private static (JArray Expanded, IReadOnlyDictionary<string, string> Iris) BindJsonLdIris(
        Stream input, JsonLdProcessorOptions options)
    {
        string source = ReadRdfUtf8(input);
        ValidateJsonLdDocument(source);
        using var reader = new JsonTextReader(new StringReader(source))
        {
            DateParseHandling = DateParseHandling.None
        };
        var document = JToken.Load(reader);
        var expanded = ExpandJsonLdDocument(document, options);
        var originals = new Dictionary<string, string>(StringComparer.Ordinal);
        var symbols = new Dictionary<string, string>(StringComparer.Ordinal);
        // A document-specific namespace avoids collisions with an explicit user IRI.
        string prefix = "urn:entityforge:jsonld:" + Guid.NewGuid().ToString("N") + ":";
        string Bind(string value)
        {
            if (value.StartsWith('@') || value.StartsWith("_:", StringComparison.Ordinal))
            {
                return value;
            }
            try
            {
                _ = new RdfIri(value);
            }
            catch (ArgumentException)
            {
                return value; // Expansion can retain relative identifiers.
            }
            // Preserve built-in datatype identities used by JSON-LD numeric conversion.
            // Ordinary absolute IRIs already retain OriginalString in this parser.
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsWellFormedOriginalString())
            {
                return value;
            }
            if (!symbols.TryGetValue(value, out string? symbol))
            {
                symbol = prefix + symbols.Count;
                symbols.Add(value, symbol);
                originals.Add(symbol, value);
            }
            return symbol;
        }
        void Visit(JToken token, bool identifiers = false)
        {
            if (token is JArray array)
            {
                foreach (var child in array.ToArray())
                {
                    Visit(child, identifiers);
                }
            }
            else if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToArray())
                {
                    // JSON literals and ordinary value strings are data, not RDF identifiers.
                    if (property.Name == "@value")
                    {
                        continue;
                    }
                    Visit(property.Value, property.Name is "@id" or "@type");
                    string name = Bind(property.Name);
                    if (name != property.Name)
                    {
                        property.Replace(new JProperty(name, property.Value));
                    }
                }
            }
            else if (identifiers && token.Type == JTokenType.String)
            {
                // Expansion can represent an ignored reserved identifier as a
                // string-typed JValue whose CLR value is null.
                string? value = token.Value<string>();
                if (value is null)
                {
                    return;
                }
                string bound = Bind(value);
                if (bound != value)
                {
                    token.Replace(new JValue(bound));
                }
            }
        }
        Visit(expanded);
        return (expanded, originals);
    }

    private static void ValidateJsonLdDocument(string source)
    {
        // Newtonsoft's permissive token reader can accept non-JSON syntax and
        // stop after the first value. Validate the complete document before any
        // expansion, without retaining a second JSON object model.
        var reader = new System.Text.Json.Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(source));
        while (reader.Read())
        {
        }
    }
}
