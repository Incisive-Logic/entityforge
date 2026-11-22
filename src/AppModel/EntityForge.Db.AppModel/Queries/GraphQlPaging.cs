using System.Text;
using System.Text.Json;
using GraphQLParser;
using GraphQLParser.AST;
using GraphQLParser.Visitors;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>Continues a single entities read without changing the editor or replaying mutations.</summary>
public static class GraphQlPaging
{
    /// <summary>Checks whether a document is a single direct entities read with safely reusable paging variables.</summary>
    public static bool CanContinue(string query)
    {
        try
        {
            Parse(query);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static (GraphQLDocument Document, GraphQLField Root) Parse(string query)
    {
        var document = Parser.Parse(query, new ParserOptions { MaxDepth = 64 });
        var operations = document.Definitions.OfType<GraphQLOperationDefinition>().ToArray();
        if (operations.Length != 1 || operations[0].Operation != OperationType.Query ||
            operations[0].SelectionSet.Selections.Count != 1 ||
            operations[0].SelectionSet.Selections[0] is not GraphQLField root || root.Name.StringValue != "entities")
        {
            throw new InvalidOperationException("Paging requires a single entities query with a direct root field.");
        }

        foreach (var argument in root.Arguments?.Where(a => a.Name.StringValue is "after" or "snapshotVersion") ?? [])
        {
            if (argument.Value is GraphQLVariable variable && System.Text.RegularExpressions.Regex.Matches(query,
                @"\$" + System.Text.RegularExpressions.Regex.Escape(variable.Name.StringValue) + @"\b").Count > 2)
            {
                throw new InvalidOperationException("Paging variables must not also control other query arguments.");
            }
        }

        return (document, root);
    }

    /// <summary>Produces a continuation query and variables using the supplied cursor and snapshot; rejects mutations and ambiguous root selections.</summary>
    public static (string Query, string Variables) Continue(string query, string variables, string cursor, long snapshot)
    {
        var (document, root) = Parse(query);
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(variables) ? "{}" : variables);
        var replacements = new Dictionary<string, JsonElement>();
        void Set(string name, string literal)
        {
            var value = Parser.Parse<GraphQLValue>(literal);
            var argument = root.Arguments?.FirstOrDefault(a => a.Name.StringValue == name);
            if (argument?.Value is GraphQLVariable variable)
            {
                using var replacement = JsonDocument.Parse(literal);
                replacements[variable.Name.StringValue] = replacement.RootElement.Clone();
            }
            else if (argument is not null)
            {
                argument.Value = value;
            }
            else
            {
                root.Arguments ??= new GraphQLArguments([]);
                root.Arguments.Items.Add(new GraphQLArgument(new GraphQLName(name), value));
            }
        }
        // JSON string escaping is also valid for a GraphQL string literal.
        using var cursorStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(cursorStream))
        {
            writer.WriteStringValue(cursor);
        }

        Set("after", Encoding.UTF8.GetString(cursorStream.ToArray()));
        Set("snapshotVersion", snapshot.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in json.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                (replacements.Remove(property.Name, out var replacement) ? replacement : property.Value).WriteTo(writer);
            }
            foreach (var replacement in replacements)
            {
                writer.WritePropertyName(replacement.Key);
                replacement.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return (new SDLPrinter().Print(document), Encoding.UTF8.GetString(stream.ToArray()));
    }
}
