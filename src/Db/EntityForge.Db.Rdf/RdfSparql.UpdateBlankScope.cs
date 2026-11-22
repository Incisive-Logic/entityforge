using System.Text.RegularExpressions;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    internal static IReadOnlySet<string> UpdateDataBlankLabels(string body)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        if (!body.Contains("_:", StringComparison.Ordinal))
        {
            return labels;
        }
        // Consume complete lexical tokens: _: text within a prefixed IRI,
        // literal or comment is not a blank-node label. This uses the same
        // normalization and name grammar as query and WHERE-scope parsing.
        foreach (Match token in Token().Matches(NormalizeQueryLexicalForm(body, RdfSparqlUpdate.MaximumCharacters)))
        {
            if (token.Value.StartsWith("_:", StringComparison.Ordinal))
            {
                labels.Add(token.Value);
            }
        }
        return labels;
    }

    internal static IReadOnlySet<string> UpdateWhereBlankLabels(string update)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        if (!update.Contains("_:", StringComparison.Ordinal))
        {
            return labels;
        }
        int depth = 0;
        bool where = false;
        // Use the same lexical normalization and tokens as query parsing so
        // comments, strings, IRIs and insert-template labels are not mistaken
        // for labels belonging to the operation's WHERE clause.
        foreach (Match token in Token().Matches(NormalizeQueryLexicalForm(update, RdfSparqlUpdate.MaximumCharacters)))
        {
            if (token.Value == "{")
            {
                depth++;
            }
            else if (token.Value == "}")
            {
                depth--;
            }
            else if (depth == 0 && token.Value.Equals("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                where = true;
            }
            else if (where && depth > 0 && token.Value.StartsWith("_:", StringComparison.Ordinal))
            {
                labels.Add(token.Value);
            }
        }
        return labels;
    }
}
