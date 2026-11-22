using System.Text.Json;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    internal const string DataPredicateShorthandIri = "urn:entityforge:predicate-shorthand";

    internal static (string Source, IReadOnlyDictionary<string, string> Iris) BindUpdateIris(string source, bool data = false)
    {
        var (body, prefixes) = ParseProlog(source, RdfSparqlUpdate.MaximumCharacters);
        var forward = new Dictionary<string, string>(StringComparer.Ordinal);
        var reverse = new Dictionary<string, string>(StringComparer.Ordinal);
        string Bind(string iri)
        {
            _ = new RdfIri(iri);
            if (!forward.TryGetValue(iri, out string? symbol))
            {
                symbol = "urn:entityforge:data-iri:" + forward.Count;
                forward.Add(iri, symbol);
                reverse.Add(symbol, iri);
            }
            return symbol;
        }
        string Rewrite(string token)
        {
            if (token == "a")
            {
                return data ? "<" + DataPredicateShorthandIri + ">" : token;
            }
            token = ExpandPrefixedName(token, prefixes);
            if (token.StartsWith('<') && token.EndsWith('>'))
            {
                return "<" + Bind(token[1..^1]) + ">";
            }
            if (token.StartsWith('"'))
            {
                int end = token.LastIndexOf('"');
                string lexical = JsonSerializer.Deserialize<string>(token[..(end + 1)])!;
                string literal = RdfSyntax.FormatTerm(new RdfLiteral(lexical));
                string suffix = token[(end + 1)..];
                return literal + (suffix.StartsWith("^^", StringComparison.Ordinal) ? "^^" + Rewrite(suffix[2..]) : suffix);
            }
            return token;
        }
        // Update syntax is still validated by the dependency. Bind every source
        // IRI before that boundary so URL canonicalization cannot merge distinct
        // RDF identifiers. Symbols are request-local and never reach storage.
        return (Token().Replace(body, match => Rewrite(match.Value)), reverse);
    }

    internal static string RestoreUpdateIris(string template, IReadOnlyDictionary<string, string> iris)
    {
        string Restore(string token)
        {
            if (token.StartsWith('<') && token.EndsWith('>') && iris.TryGetValue(token[1..^1], out string? original))
            {
                return "<" + original + ">";
            }
            if (token.StartsWith('"'))
            {
                int end = token.LastIndexOf('"');
                string suffix = token[(end + 1)..];
                if (suffix.StartsWith("^^", StringComparison.Ordinal))
                {
                    return token[..(end + 1)] + "^^" + Restore(suffix[2..]);
                }
            }
            return token;
        }
        return Token().Replace(template, match => Restore(match.Value));
    }
}
