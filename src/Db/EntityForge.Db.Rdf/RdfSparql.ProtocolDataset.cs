using System.Text;
using System.Text.RegularExpressions;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // Render through the native dataset grammar so local and distributed execution
    // share exactly the same snapshot, merge and resource-limit semantics.
    internal static string WithProtocolDataset(string query, IReadOnlyList<string> defaults, IReadOnlyList<string> named)
    {
        if (defaults.Count + named.Count == 0)
        {
            return query;
        }
        if (defaults.Count + named.Count > 16)
        {
            throw new RdfSparqlWorkLimitException("A query accepts at most 16 dataset parameters.");
        }
        string Clause(string value, bool isNamed)
        {
            return (isNamed ? " FROM NAMED " : " FROM ") + RdfSyntax.FormatTerm(new RdfIri(value));
        }
        string clauses = String.Concat(defaults.Select(value => Clause(value, false))) +
            String.Concat(named.Select(value => Clause(value, true))) + " ";
        query = ParseDataset(query)?.Query ?? query;
        var (body, prefixes) = ParseProlog(query);
        bool template = Regex.IsMatch(body, @"^\s*CONSTRUCT\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int braces = 0, parentheses = 0, insertion = body.Length;
        bool quoted = false, iri = false;
        for (int index = 0; index < body.Length; index++)
        {
            char character = body[index];
            if (quoted)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                continue;
            }
            if (iri)
            {
                if (character == '>')
                {
                    iri = false;
                }
                continue;
            }
            if (character == '"')
            {
                quoted = true;
                continue;
            }
            if (character == '<' && FindLexicalIriEnd(body, index) >= 0)
            {
                iri = true;
                continue;
            }
            // Escaped parentheses and keyword-like suffixes belong to the
            // prefixed name, not to the surrounding query structure.
            var nameToken = DatasetNameToken().Match(body, index);
            if (nameToken.Success)
            {
                index += nameToken.Length - 1;
                continue;
            }
            if (character == '(')
            {
                parentheses++;
            }
            if (character == ')')
            {
                parentheses--;
            }
            if (braces == 0 && parentheses == 0 &&
                ((character == '{' && !template) ||
                 (index > 0 && (Char.IsWhiteSpace(body[index - 1]) || body[index - 1] is '}' or '>') &&
                  Regex.IsMatch(body[index..], @"^(?:WHERE|GROUP|HAVING|ORDER|LIMIT|OFFSET)(?=\s|\{)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))))
            {
                insertion = index;
                break;
            }
            if (character == '{')
            {
                braces++;
            }
            if (character == '}' && --braces == 0)
            {
                template = false;
            }
        }
        var prolog = new StringBuilder();
        if (prefixes.TryGetValue("$base", out string? baseIri))
        {
            prolog.Append("BASE <").Append(baseIri).Append("> ");
        }
        foreach (var prefix in prefixes.Where(pair => pair.Key != "$base"))
        {
            prolog.Append("PREFIX ").Append(prefix.Key).Append(": <").Append(prefix.Value).Append("> ");
        }
        return prolog.Append(body.AsSpan(0, insertion)).Append(clauses).Append(body.AsSpan(insertion)).ToString();
    }
}
