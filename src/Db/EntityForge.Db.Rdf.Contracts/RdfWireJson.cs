using System.Text.Json;

namespace EntityForge.Db.Rdf.Contracts;

internal static class RdfWireJson
{
    internal static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
        ? field : throw new InvalidDataException($"Missing RDF member '{name}'.");

    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! :
        throw new InvalidDataException("Expected an RDF string.");

    private static string? Optional(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Text(value);

    internal static void UniqueMembers(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Duplicate RDF member.");
                }

                UniqueMembers(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                UniqueMembers(item);
            }
        }
    }

    internal static RdfQuad Quad(JsonElement value) => new(Term(Field(value, "subject")),
        Predicate(Field(value, "predicate")), Term(Field(value, "object")),
        Field(value, "graph").ValueKind == JsonValueKind.Null ? null : Term(Field(value, "graph")));

    internal static RdfTerm Term(JsonElement value, int depth = 0)
    {
        if (depth > RdfTripleTerm.MaximumNestingDepth)
        {
            throw new InvalidDataException("Quoted RDF nesting exceeds its bound.");
        }

        return Text(Field(value, "$term")) switch
        {
            "iri" => new RdfIri(Text(Field(value, "value"))),
            "blank" => new RdfBlankNode(Text(Field(value, "identifier"))),
            "literal" => new RdfLiteral(Text(Field(value, "lexicalForm")), Optional(Field(value, "datatype")), Optional(Field(value, "language"))),
            "triple" => new RdfTripleTerm(Term(Field(value, "subject"), depth + 1), Predicate(Field(value, "predicate")), Term(Field(value, "object"), depth + 1)),
            _ => throw new InvalidDataException("Unknown RDF term discriminator.")
        };
    }

    private static RdfIri Predicate(JsonElement value)
    {
        if (value.TryGetProperty("$term", out var tag) && Text(tag) != "iri")
        {
            throw new InvalidDataException("An RDF predicate must be an IRI.");
        }

        return new(Text(Field(value, "value")));
    }
}
