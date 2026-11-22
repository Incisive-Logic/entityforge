using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlMutationCompiler
{
    private static string[] ExpandCreationPatterns(string source, IReadOnlySet<string>? inputScope = null)
    {
        var nodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var edges = new List<string>();
        var reserved = GqlRegex.Matches(source, GraphGqlIdentifiers.Pattern).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        if (inputScope is not null)
        {
            reserved.UnionWith(inputScope);
        }
        int anonymous = 0;
        foreach (string item in SplitTopLevel(source))
        {
            int position = 0;
            void Space()
            {
                while (position < item.Length && Char.IsWhiteSpace(item[position]))
                {
                    position++;
                }
            }
            void Require(string text)
            {
                Space();
                if (!item.AsSpan(position).StartsWith(text, StringComparison.Ordinal))
                {
                    throw new ArgumentException("A creation pattern requires nodes connected by typed relationships.");
                }
                position += text.Length;
            }
            string Delimited(char open, char close)
            {
                Require(open.ToString());
                int start = position, depth = 1;
                bool quoted = false, escaped = false;
                while (position < item.Length)
                {
                    char c = item[position++];
                    if (quoted)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (c == '\\')
                        {
                            escaped = true;
                        }
                        else if (c == '"')
                        {
                            quoted = false;
                        }
                        continue;
                    }
                    if (c == '"')
                    {
                        quoted = true;
                    }
                    else if (c == open)
                    {
                        depth++;
                    }
                    else if (c == close && --depth == 0)
                    {
                        return item[start..(position - 1)];
                    }
                }
                throw new ArgumentException("An unclosed creation pattern is invalid.");
            }
            string Node()
            {
                string body = Delimited('(', ')').Trim();
                var binding = GqlRegex.Match(body, @"^" + GraphGqlIdentifiers.Pattern);
                string name = binding.Value;
                string tail = body[binding.Length..].Trim();
                if (name.Length == 0)
                {
                    do
                    {
                        name = "__created" + anonymous++;
                    }
                    while (!reserved.Add(name));
                }
                if (nodes.ContainsKey(name))
                {
                    if (tail.Length != 0)
                    {
                        throw new ArgumentException("A bound creation node cannot be redeclared with labels or properties.");
                    }
                }
                else
                {
                    nodes.Add(name, "(" + name + tail + ")");
                }
                return name;
            }
            string previous = Node();
            Space();
            while (position < item.Length)
            {
                bool incoming = item.AsSpan(position).StartsWith("<-", StringComparison.Ordinal);
                Require(incoming ? "<-" : "-");
                string edge = Delimited('[', ']');
                Require(incoming ? "-" : "->");
                string next = Node();
                edges.Add("(" + (incoming ? next : previous) + ")-[" + edge + "]->(" + (incoming ? previous : next) + ")");
                previous = next;
                Space();
            }
        }
        return nodes.Values.Concat(edges).ToArray();
    }
}
