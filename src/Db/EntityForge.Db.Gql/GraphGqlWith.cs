using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    // The internal graph projection is a complete projection, not any
    // expression beginning with the non-reserved identifier GRAPH. Standard
    // lowering makes a bare binding explicit as GRAPH AS GRAPH.
    private static bool IsWholeGraphProjection(string projection) => GqlRegex.IsMatch(projection,
        @"^\s*GRAPH(?:\s*$|\s+(?:ORDER\s+BY\b|LIMIT\b|SKIP\b|OFFSET\b))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static List<(string Word, int Start, int Length)> WithClauses(string source, bool mutations = false)
    {
        string masked = MaskQuotedStrings(source);
        var result = new List<(string Word, int Start, int Length)>();
        int depth = 0;
        for (int index = 0; index < masked.Length; index++)
        {
            if (masked[index] is '(' or '[' or '{')
            {
                depth++;
            }
            else if (masked[index] is ')' or ']' or '}')
            {
                depth--;
            }
            if (depth != 0 || index > 0 && (Char.IsLetterOrDigit(masked[index - 1]) || masked[index - 1] is '_' or '.' or ':' or '$'))
            {
                continue;
            }
            Match match = (mutations ? MutationClauseWord : WithClauseWord).Match(masked, index);
            if (!match.Success)
            {
                continue;
            }
            string word = GqlRegex.Replace(match.Value.ToUpperInvariant(), @"\s+", " ");
            if (mutations && word is "MATCH" or "CREATE" && GqlRegex.IsMatch(masked[..index], @"\bON\s+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }
            var previous = result.LastOrDefault();
            bool firstOperand = result.Count > 0 && previous.Word is "RETURN" or "WITH" or "WHERE" or "FILTER" or "ORDER BY" or "GROUP BY" or "UNWIND" or "LET" or "SET" or "REMOVE" or "DELETE" &&
                GqlRegex.IsMatch(masked[(previous.Start + previous.Length)..index], @"^\s*(?:(?:DISTINCT|ALL)\s+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!word.Contains(' ') && (firstOperand || GqlRegex.IsMatch(masked[..index], @"(?:\bAS|\bIS\s+(?:NOT\s+)?LABELED)\s+$|[,=]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            {
                continue;
            }
            // STARTS WITH and ENDS WITH are operators, not stage boundaries.
            if (word == "WITH" && (GqlRegex.IsMatch(masked[..index], @"(?:STARTS|ENDS)\s+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                GqlRegex.IsMatch(masked[..index], @"\b(?:TIME|TIMESTAMP)\s+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
                GqlRegex.IsMatch(masked[(index + match.Length)..], @"^\s+TIME\s+ZONE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            {
                continue;
            }
            result.Add((word, index, match.Length));
            index += match.Length - 1;
        }
        return result;
    }

    private static readonly Regex WithClauseWord = new(@"\G(OPTIONAL\s+MATCH|OPTIONAL\s+CALL|ORDER\s+BY|GROUP\s+BY|WITH|WHERE|FILTER|RETURN|MATCH|LIMIT|SKIP|OFFSET|UNWIND|FOR|LET|UNION|CALL)(?!" + GraphGqlIdentifiers.Continuation + @"|[.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex MutationClauseWord = new(@"\G(DETACH\s+DELETE|NODETACH\s+DELETE|OPTIONAL\s+MATCH|OPTIONAL\s+CALL|ORDER\s+BY|GROUP\s+BY|CREATE|INSERT|MERGE|SET|REMOVE|DELETE|FOREACH|WITH|WHERE|FILTER|RETURN|MATCH|LIMIT|SKIP|OFFSET|UNWIND|FOR|LET|UNION|CALL)(?!" + GraphGqlIdentifiers.Continuation + @"|[.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
