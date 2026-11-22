using System.Text;
using System.Text.Json;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    internal static string NormalizeDependencyPrefixNames(string source, string prolog)
    {
        if (!prolog.Any(Char.IsSurrogate))
        {
            return source;
        }
        var (_, prefixes) = ParseProlog(prolog);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var occupied = prefixes.Keys.ToHashSet(StringComparer.Ordinal);
        int next = 0;
        foreach (string prefix in prefixes.Keys.Where(key => key.Any(Char.IsSurrogate)))
        {
            string alias;
            do
            {
                alias = "entityforgePrefix" + next++;
            }
            while (!occupied.Add(alias));
            aliases.Add(prefix, alias);
        }
        if (aliases.Count == 0)
        {
            return source;
        }
        // The dependency treats UTF-16 code units as prefix characters. Rename
        // only namespace labels for validation; preserve IRIs and literal text.
        // Normalize strings/comments first so token replacement cannot edit them.
        string body = NormalizeQueryLexicalForm(source[prolog.Length..], RdfSparqlUpdate.MaximumCharacters);
        string Rename(string token)
        {
            if (token.StartsWith('"'))
            {
                int end = token.LastIndexOf('"');
                string lexical = JsonSerializer.Deserialize<string>(token[..(end + 1)])!;
                // JSON normalisation can emit paired UTF-16 escapes. Emit RDF
                // lexical text instead, since the dependency expects scalar escapes.
                string literal = RdfSyntax.FormatTerm(new RdfLiteral(lexical));
                string suffix = token[(end + 1)..];
                return literal + (suffix.StartsWith("^^", StringComparison.Ordinal) ? "^^" + Rename(suffix[2..]) : suffix);
            }
            int separator = token.IndexOf(':');
            return separator >= 0 && aliases.TryGetValue(token[..separator], out string? alias)
                ? alias + token[separator..] : token;
        }
        var result = new StringBuilder();
        foreach (var pair in prefixes)
        {
            if (pair.Key == "$base")
            {
                result.Append("BASE <").Append(pair.Value).Append("> ");
            }
            else
            {
                result.Append("PREFIX ").Append(aliases.GetValueOrDefault(pair.Key, pair.Key))
                    .Append(": <").Append(pair.Value).Append("> ");
            }
        }
        return result.Append(Token().Replace(body, match => Rename(match.Value))).ToString();
    }
}
