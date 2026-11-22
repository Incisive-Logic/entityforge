namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private sealed record GraphQueryShape(string? Template, string Body, string Tail);

    private static GraphQueryShape ParseGraphQueryShape(string source, IReadOnlyDictionary<string, string> prefixes, bool construct)
    {
        source = source.TrimStart();
        string keyword = construct ? "CONSTRUCT" : "ASK";
        if (!source.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Unsupported SPARQL graph query form.");
        }
        string[] tokens = Tokenize(source[keyword.Length..], prefixes);
        int index = 0;
        string GroupText()
        {
            if (index >= tokens.Length || tokens[index++] != "{")
            {
                throw new NotSupportedException("A SPARQL query group is required.");
            }
            int start = index;
            int depth = 1;
            while (index < tokens.Length)
            {
                string token = tokens[index++];
                if (token == "{")
                {
                    depth++;
                }
                if (token == "}" && --depth == 0)
                {
                    return string.Join(" ", tokens[start..(index - 1)]);
                }
            }
            throw new NotSupportedException("Unclosed SPARQL query group.");
        }
        string? template = construct && tokens.FirstOrDefault() == "{" ? GroupText() : null;
        if (index < tokens.Length && tokens[index].Equals("WHERE", StringComparison.OrdinalIgnoreCase))
        {
            index++;
        }
        string body = GroupText();
        return new(template, body, string.Join(" ", tokens[index..]));
    }
}
