using System.Text.Json;
using System.Text.RegularExpressions;
using GraphQLParser;
using GraphQLParser.Exceptions;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>Describes a token span used for GraphQL editor highlighting.</summary>
/// <param name="Start">The zero-based character offset in the editor text.</param>
/// <param name="Length">The token's character length.</param>
/// <param name="Text">The token text.</param>
/// <param name="Kind">The token classification used for highlighting.</param>
public sealed record GraphQlEditorToken(int Start, int Length, string Text, string Kind);

/// <summary>Associates a GraphQL editor diagnostic with a source-text span.</summary>
/// <param name="Start">The zero-based character offset of the diagnostic.</param>
/// <param name="Length">The highlighted character count.</param>
/// <param name="Message">The diagnostic explanation presented to the user.</param>
public sealed record GraphQlEditorDiagnostic(int Start, int Length, string Message);

/// <summary>Describes one schema-aware GraphQL completion item.</summary>
/// <param name="Name">The completion label.</param>
/// <param name="InsertText">The text inserted when the item is selected.</param>
/// <param name="Description">The schema or usage explanation shown with the item.</param>
public sealed record GraphQlCompletion(string Name, string InsertText, string Description);

/// <summary>Provides completion candidates and the text range they replace.</summary>
/// <param name="Start">The zero-based start of the replacement range.</param>
/// <param name="Length">The number of characters to replace.</param>
/// <param name="Items">The available completion candidates.</param>
public sealed record GraphQlCompletions(int Start, int Length, IReadOnlyList<GraphQlCompletion> Items);

/// <summary>Bounded, local editor assistance. The host remains authoritative for operation validation.</summary>
public sealed class GraphQlEditorLanguage
{
    private sealed record TypeRef(string Kind, string? Name, TypeRef? OfType)
    {
        public string Named => Name ?? OfType?.Named ?? "";

        public override string ToString() => Kind == "NON_NULL" ? OfType + "!" : Kind == "LIST" ? "[" + OfType + "]" : Name ?? "?";
    }

    private sealed record Member(string Name, string? Description, TypeRef Type, bool Required, Dictionary<string, Member> Arguments);

    private sealed record SchemaType(string Kind, Dictionary<string, Member> Members);

    private readonly Dictionary<string, SchemaType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _roots = new(StringComparer.Ordinal);
    private static readonly Regex Tokens = new("""(?<Comment>\#[^\r\n]*)|(?<String>""" + "\"\"\"[\\s\\S]*?(?:\"\"\"|$)|\"(?:\\\\.|[^\"\\\\\\r\\n])*\"?)" + """|(?<Variable>\$[_A-Za-z][_0-9A-Za-z]*)|(?<Directive>@[_A-Za-z][_0-9A-Za-z]*)|(?<Number>-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|(?<Name>[_A-Za-z][_0-9A-Za-z]*)|(?<Punctuation>\.\.\.|[^\s,\uFEFF])""", RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    /// <summary>Tokenizes GraphQL source for editor presentation.</summary>
    public static IReadOnlyList<GraphQlEditorToken> Tokenize(string text)
    {
        if (text.Length > HttpNativeQueryExecutor.MaximumQueryCharacters)
        {
            return [];
        }

        return Tokens.Matches(text).Select(m =>
        {
            string kind = new[] { "Comment", "String", "Variable", "Directive", "Number", "Name", "Punctuation" }.First(n => m.Groups[n].Success);
            if (kind == "Name" && m.Value is "query" or "mutation" or "subscription" or "fragment" or "on" or "true" or "false" or "null")
            {
                kind = "Keyword";
            }

            return new GraphQlEditorToken(m.Index, m.Length, m.Value, kind);
        }).ToArray();
    }

    /// <summary>The GraphQL introspection document used to obtain editor schema metadata.</summary>
    public static string IntrospectionQuery { get; } = BuildIntrospection();

    private static string BuildIntrospection()
    {
        string type = "kind name";
        for (int i = 0; i < 7; i++)
        {
            type = "kind name ofType { " + type + " }";
        }

        string input = "name description defaultValue type { " + type + " }";
        return "{ __schema { queryType { name } mutationType { name } subscriptionType { name } types { kind name " +
            "fields(includeDeprecated:true) { name description type { " + type + " } args { " + input + " } } " +
            "inputFields { " + input + " } enumValues(includeDeprecated:true) { name description } } } }";
    }

    /// <summary>Builds editor metadata from a GraphQL introspection result.</summary>
    public static GraphQlEditorLanguage FromIntrospection(JsonElement response)
    {
        var language = new GraphQlEditorLanguage();
        var schema = response.GetProperty("data").GetProperty("__schema");
        foreach (string operation in new[] { "query", "mutation", "subscription" })
        {
            if (schema.TryGetProperty(operation + "Type", out var root) && root.ValueKind == JsonValueKind.Object)
            {
                language._roots[operation] = root.GetProperty("name").GetString()!;
            }
        }

        foreach (var type in schema.GetProperty("types").EnumerateArray())
        {
            var members = new Dictionary<string, Member>(StringComparer.Ordinal);
            foreach (string collection in new[] { "fields", "inputFields", "enumValues" })
            {
                if (type.TryGetProperty(collection, out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var member in values.EnumerateArray())
                    {
                        members[member.GetProperty("name").GetString()!] = ReadMember(member);
                    }
                }
            }

            language._types[type.GetProperty("name").GetString()!] = new(type.GetProperty("kind").GetString()!, members);
        }
        return language;
    }

    private static TypeRef ReadType(JsonElement value) => new(value.GetProperty("kind").GetString()!,
        value.TryGetProperty("name", out var name) ? name.GetString() : null,
        value.TryGetProperty("ofType", out var child) && child.ValueKind == JsonValueKind.Object ? ReadType(child) : null);

    private static Member ReadMember(JsonElement member)
    {
        var type = member.TryGetProperty("type", out var t) ? ReadType(t) : new TypeRef("ENUM_VALUE", "", null);
        var args = new Dictionary<string, Member>(StringComparer.Ordinal);
        if (member.TryGetProperty("args", out var a))
        {
            foreach (var arg in a.EnumerateArray())
            {
                args[arg.GetProperty("name").GetString()!] = ReadMember(arg);
            }
        }

        return new(member.GetProperty("name").GetString()!, member.TryGetProperty("description", out var d) ? d.GetString() : null, type,
            type.Kind == "NON_NULL" && (!member.TryGetProperty("defaultValue", out var def) || def.ValueKind == JsonValueKind.Null), args);
    }

    /// <summary>Returns schema-aware completions for the current editor position.</summary>
    public GraphQlCompletions Complete(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var tokens = Tokenize(text[..offset]);
        if (tokens.LastOrDefault() is { } last && last.Start + last.Length == offset && last.Kind is "Comment" or "String")
        {
            return new(offset, 0, []);
        }

        string prefix = "";
        int start = offset;
        if (tokens.LastOrDefault() is { Kind: "Name" or "Keyword" } word && word.Start + word.Length == offset)
        {
            start = word.Start;
            prefix = word.Text;
            tokens = tokens.Take(tokens.Count - 1).ToArray();
        }
        var walker = new Walker(this, tokens, true);
        walker.Document();
        return new(start, offset - start, walker.Completions.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Returns syntax or schema diagnostics for the current GraphQL document.</summary>
    public IReadOnlyList<GraphQlEditorDiagnostic> Validate(string text)
    {
        if (text.Length > HttpNativeQueryExecutor.MaximumQueryCharacters)
        {
            return [new(0, 1, "The operation exceeds the text limit.")];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            Parser.Parse(text, new ParserOptions { MaxDepth = 64 });
        }
        catch (GraphQLSyntaxErrorException error)
        {
            // Parser locations are one-based lines/columns; editor offsets are UTF-16.
            int offset = 0, line = 1;
            while (offset < text.Length && line < error.Location.Line)
            {
                if (text[offset++] == '\n')
                {
                    line++;
                }
            }

            offset = Math.Clamp(offset + error.Location.Column - 1, 0, Math.Max(0, text.Length - 1));
            string message = error.Description;
            int documentation = message.IndexOf("; for more information see", StringComparison.Ordinal);
            if (documentation >= 0)
            {
                message = message[..documentation];
            }

            return [new(offset, 1, message)];
        }
        if (_types.Count == 0)
        {
            return [];
        }

        var walker = new Walker(this, Tokenize(text), false);
        walker.Document();
        return walker.Diagnostics.Take(20).ToArray();
    }

    private Dictionary<string, Member> Members(string? name) => name is not null && _types.TryGetValue(name, out var type) ? type.Members : [];

    // This tolerant walk also works on incomplete text before the caret. It never executes a document.
    private sealed class Walker(GraphQlEditorLanguage schema, IReadOnlyList<GraphQlEditorToken> input, bool completing)
    {
        private readonly GraphQlEditorToken[] _tokens = input.Where(t => t.Kind != "Comment").ToArray();
        private int _index;

        public List<GraphQlCompletion> Completions { get; } = [];

        public List<GraphQlEditorDiagnostic> Diagnostics { get; } = [];

        private bool End => _index >= _tokens.Length;

        private string Peek => End ? "" : _tokens[_index].Text;

        private GraphQlEditorToken Take() => End ? new(0, 0, "", "") : _tokens[_index++];

        private bool Eat(string value)
        {
            if (Peek != value)
            {
                return false;
            }

            _index++;
            return true;
        }

        private void Suggest(IEnumerable<Member> members, bool input = false)
        {
            if (!End || !completing)
            {
                return;
            }

            Completions.AddRange(members.Select(m => new GraphQlCompletion(m.Name, m.Name + (input ? ": " : ""),
                m.Name + ": " + m.Type + (input && m.Required ? " (required)" : "") +
                (m.Arguments.Count == 0 ? "" : "\nArguments: " + string.Join(", ", m.Arguments.Values.Select(a => a.Name + ": " + a.Type + (a.Required ? " (required)" : "")))) +
                (string.IsNullOrWhiteSpace(m.Description) ? "" : "\n" + m.Description))));
        }

        private void Error(GraphQlEditorToken token, string message)
        {
            if (!completing)
            {
                Diagnostics.Add(new(token.Start, Math.Max(1, token.Length), message));
            }
        }

        public void Document()
        {
            while (!End)
            {
                if (Peek == "{")
                {
                    Selection(schema._roots.GetValueOrDefault("query"), 0);
                    continue;
                }
                string operation = Take().Text;
                if (operation == "fragment")
                {
                    Take();
                    Eat("on");
                    string type = Take().Text;
                    Directives();
                    Selection(type, 0);
                    continue;
                }
                if (!schema._roots.ContainsKey(operation))
                {
                    continue;
                }

                if (Peek is not ("{" or "(" or "") && !Peek.StartsWith('@'))
                {
                    Take();
                }

                if (Peek == "(")
                {
                    SkipBalanced("(", ")");
                }

                Directives();
                Selection(schema._roots[operation], 0);
            }
            if (Completions.Count == 0 && completing && _tokens.Length == 0)
            {
                Completions.AddRange(schema._roots.Keys.Select(k => new GraphQlCompletion(k, k + " {\n  \n}", k + " operation")));
            }
        }

        private void Selection(string? type, int depth)
        {
            if (depth > 32 || !Eat("{"))
            {
                return;
            }

            var members = schema.Members(type);
            while (!End && Peek != "}")
            {
                if (Eat("..."))
                {
                    if (Eat("on"))
                    {
                        string condition = Take().Text;
                        Directives();
                        Selection(condition, depth + 1);
                    }
                    else
                    {
                        Take();
                        Directives();
                    }
                    continue;
                }
                var token = Take();
                if (Eat(":"))
                {
                    if (End)
                    {
                        Suggest(members.Values);
                        return;
                    }
                    token = Take();
                }
                members.TryGetValue(token.Text, out var member);
                if (token.Text == "__schema")
                {
                    member = new("__schema", null, new("OBJECT", "__Schema", null), false, []);
                }

                if (token.Text == "__type")
                {
                    member = new("__type", null, new("OBJECT", "__Type", null), false,
                    new()
                    {
                        ["name"] = new("name", null, new("NON_NULL", null, new("SCALAR", "String", null)), true, [])
                    });
                }

                if (token.Text == "__typename")
                {
                    member = new("__typename", null, new("SCALAR", "String", null), false, []);
                }

                if (member is null && !token.Text.StartsWith("__", StringComparison.Ordinal) && type is not null)
                {
                    Error(token, $"Unknown field '{token.Text}' on {type}.");
                }

                var used = new HashSet<string>();
                if (Eat("("))
                {
                    Arguments(member?.Arguments ?? [], used, depth);
                    if (End && completing)
                    {
                        return;
                    }
                }
                if (!End || !completing)
                {
                    foreach (var required in (member?.Arguments.Values.AsEnumerable() ?? []).Where(a => a.Required && !used.Contains(a.Name)))
                    {
                        Error(token, $"{token.Text} requires argument '{required.Name}: {required.Type}'.");
                    }
                }

                Directives();
                if (Peek == "{")
                {
                    Selection(member?.Type.Named, depth + 1);
                }
            }
            if (End)
            {
                Suggest(members.Values);
            }
            else
            {
                Eat("}");
            }
        }

        private void Arguments(Dictionary<string, Member> members, HashSet<string> used, int depth)
        {
            while (!End && Peek != ")")
            {
                var token = Take();
                used.Add(token.Text);
                members.TryGetValue(token.Text, out var member);
                if (member is null)
                {
                    Error(token, $"Unknown argument '{token.Text}'.");
                }

                if (!Eat(":"))
                {
                    break;
                }

                Value(member?.Type, depth + 1);
                if (End && completing && Completions.Count > 0)
                {
                    return;
                }
            }
            if (End)
            {
                Suggest(members.Values.Where(m => !used.Contains(m.Name)), true);
            }
            else
            {
                Eat(")");
            }
        }

        private void Value(TypeRef? type, int depth)
        {
            if (depth > 64)
            {
                _index = _tokens.Length;
                return;
            }
            bool nonNull = type?.Kind == "NON_NULL";
            if (type?.Kind == "NON_NULL")
            {
                type = type.OfType;
            }

            if (End)
            {
                if (type?.Named == "Boolean")
                {
                    Completions.AddRange(new[] { "true", "false" }.Select(v => new GraphQlCompletion(v, v, "Boolean")));
                }
                else if (type is not null && schema._types.TryGetValue(type.Named, out var t) && t.Kind == "ENUM")
                {
                    Suggest(t.Members.Values);
                }

                return;
            }
            if (Eat("["))
            {
                while (!End && Peek != "]")
                {
                    Value(type?.OfType ?? type, depth + 1);
                }

                Eat("]");
                return;
            }
            if (Eat("{"))
            {
                if (type?.Named == "JSON")
                {
                    _index--;
                    SkipBalanced("{", "}");
                    return;
                }
                var members = schema.Members(type?.Named);
                var used = new HashSet<string>();
                var start = _tokens[Math.Max(0, _index - 1)];
                while (!End && Peek != "}")
                {
                    var token = Take();
                    used.Add(token.Text);
                    members.TryGetValue(token.Text, out var member);
                    if (member is null && type?.Named != "JSON")
                    {
                        Error(token, $"Unknown input field '{token.Text}' on {type?.Named}.");
                    }

                    if (!Eat(":"))
                    {
                        break;
                    }

                    Value(member?.Type, depth + 1);
                    if (End && completing && Completions.Count > 0)
                    {
                        return;
                    }
                }
                if (End)
                {
                    Suggest(members.Values.Where(m => !used.Contains(m.Name)), true);
                }
                else
                {
                    Eat("}");
                    foreach (var required in members.Values.Where(m => m.Required && !used.Contains(m.Name)))
                    {
                        Error(start, $"{type?.Named} requires '{required.Name}: {required.Type}'.");
                    }
                }
                return;
            }
            var value = Take();
            if (value.Text == "null" && nonNull)
            {
                Error(value, "This value cannot be null.");
            }

            if (value.Kind != "Variable" && value.Text != "null" && type?.Named is "String" or "Int" or "Float" or "Boolean")
            {
                bool valid = type.Named switch
                {
                    "String" => value.Kind == "String",
                    "Boolean" => value.Text is "true" or "false",
                    "Int" => int.TryParse(value.Text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _),
                    _ => value.Kind == "Number"
                };
                if (!valid)
                {
                    Error(value, $"Expected a {type.Named} value.");
                }
            }
            if (value.Kind != "Variable" && value.Text != "null" && type is not null && schema._types.TryGetValue(type.Named, out var enumType) && enumType.Kind == "ENUM" && !enumType.Members.ContainsKey(value.Text))
            {
                Error(value, $"Unknown {type.Named} value '{value.Text}'.");
            }
        }

        private void Directives()
        {
            while (Peek.StartsWith('@'))
            {
                Take();
                if (Peek == "(")
                {
                    SkipBalanced("(", ")");
                }
            }
        }

        private void SkipBalanced(string open, string close)
        {
            int depth = 0;
            do
            {
                var token = Take();
                if (token.Text == open)
                {
                    depth++;
                }
                else if (token.Text == close)
                {
                    depth--;
                }
            } while (!End && depth > 0);
        }
    }
}
