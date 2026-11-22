using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    private static readonly Regex AggregateStart = new(
        @"(?<!" + GraphGqlIdentifiers.Continuation + @")(?<![.])(?:COUNT|SUM|MIN|MAX|AVG|COLLECT|COLLECT_LIST|STDEV|STDEVP|STDDEV_SAMP|STDDEV_POP|STDEV_SAMP|STDEV_POP|PERCENTILECONT|PERCENTILEDISC|PERCENTILE_CONT|PERCENTILE_DISC)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Extract aggregate calls into one grouping stage, then evaluate scalar
    // expressions over the resulting aggregate/group columns. Native aggregate
    // execution remains responsible for empty-input and DISTINCT semantics.
    private static string LowerAggregateExpressions(string source)
    {
        var clauses = WithClauses(source);
        for (int clauseIndex = clauses.Count - 1; clauseIndex >= 0; clauseIndex--)
        {
            var clause = clauses[clauseIndex];
            if (clause.Word is not ("WITH" or "RETURN"))
            {
                continue;
            }
            int end = clauseIndex + 1 < clauses.Count ? clauses[clauseIndex + 1].Start : source.Length;
            string body = source[(clause.Start + clause.Length)..end].Trim().TrimEnd(';');
            bool distinct = GqlRegex.IsMatch(body, @"^DISTINCT\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (distinct)
            {
                body = body[8..].TrimStart();
            }
            else if (GqlRegex.IsMatch(body, @"^ALL\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                body = body[3..].TrimStart();
            }
            var items = SplitProjectionItems(body).Select(item =>
            {
                var alias = GraphGqlIdentifiers.ProjectionAlias(item);
                string expression = alias.Success ? item[..alias.Index].Trim() : item.Trim();
                string name = alias.Success ? GraphGqlIdentifiers.DecodeAlias(alias) : expression;
                string masked = MaskReadSubqueryAggregates(MaskQuotedStrings(expression));
                var calls = new List<(int Start, int Length)>();
                int position = 0;
                while (AggregateStart.Match(masked, position) is { Success: true } match)
                {
                    int closing = match.Index + match.Length;
                    int depth = 1;
                    while (closing < masked.Length && depth != 0)
                    {
                        if (masked[closing] == '(')
                        {
                            depth++;
                        }
                        if (masked[closing] == ')')
                        {
                            depth--;
                        }
                        closing++;
                    }
                    if (depth != 0)
                    {
                        throw Invalid("An aggregate expression has an unmatched parenthesis.");
                    }
                    calls.Add((match.Index, closing - match.Index));
                    position = closing;
                }
                return (Expression: expression, Name: name, ExplicitAlias: alias.Success, Calls: calls);
            }).ToArray();
            if (!items.Any(item => item.Calls.Count > 0 &&
                (item.Calls.Count != 1 || item.Calls[0].Start != 0 || item.Calls[0].Length != item.Expression.Length)))
            {
                continue;
            }
            var grouped = new List<string>();
            var projected = new List<string>();
            var groupingAliases = new Dictionary<string, string>(StringComparer.Ordinal);
            string NewAlias()
            {
                string candidate = "__aggregate_value_" + grouped.Count;
                while (GqlRegex.IsMatch(MaskQuotedStrings(source), @"\b" + candidate + @"\b"))
                {
                    candidate += "_";
                }
                return candidate;
            }
            foreach (var item in items)
            {
                if (!item.ExplicitAlias && !GraphGqlIdentifiers.Whole.IsMatch(item.Name))
                {
                    throw Invalid("A computed aggregate column requires AS and an alias.");
                }
                string expression = item.Expression;
                if (item.Calls.Count == 0)
                {
                    string alias = NewAlias();
                    grouped.Add(expression + " AS " + alias);
                    groupingAliases[item.Name] = alias;
                    groupingAliases.TryAdd(expression, alias);
                    expression = alias;
                }
                else
                {
                    foreach (var call in item.Calls.AsEnumerable().Reverse())
                    {
                        string alias = NewAlias();
                        grouped.Add(expression.Substring(call.Start, call.Length) + " AS " + alias);
                        expression = expression[..call.Start] + alias + expression[(call.Start + call.Length)..];
                    }
                }
                projected.Add(expression + " AS " + GraphGqlIdentifiers.QuoteAlias(item.Name));
            }
            string grouping = "";
            if (clauseIndex + 1 < clauses.Count && clauses[clauseIndex + 1].Word == "GROUP BY")
            {
                var groupClause = clauses[clauseIndex + 1];
                end = clauseIndex + 2 < clauses.Count ? clauses[clauseIndex + 2].Start : source.Length;
                string keys = source[(groupClause.Start + groupClause.Length)..end].Trim();
                grouping = " GROUP BY " + (keys == "()" ? "()" : String.Join(",",
                    SplitProjectionItems(keys).Select(key => groupingAliases.TryGetValue(key.Trim(), out string? alias)
                        ? alias : throw Invalid("GROUP BY requires a non-aggregate projected key."))));
            }
            source = source[..clause.Start] + "WITH " + String.Join(",", grouped) + grouping + " " + clause.Word + " " +
                (distinct ? "DISTINCT " : "") + String.Join(",", projected) + " " + source[end..];
        }
        if (source.Length > MaximumQueryCharacters)
        {
            throw Invalid("The expanded aggregate query exceeds the character limit.");
        }
        return source;
    }

    private static string MaskReadSubqueryAggregates(string masked)
    {
        var characters = masked.ToCharArray();
        int covered = 0;
        foreach (Match match in GqlRegex.Matches(masked, @"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (match.Index < covered)
            {
                continue;
            }
            int end = match.Index + match.Length, depth = 1;
            while (end < masked.Length && depth != 0)
            {
                if (masked[end] == '{')
                {
                    depth++;
                }
                else if (masked[end] == '}')
                {
                    depth--;
                }
                end++;
            }
            Array.Fill(characters, ' ', match.Index, end - match.Index);
            covered = end;
        }
        return new string(characters);
    }
}
