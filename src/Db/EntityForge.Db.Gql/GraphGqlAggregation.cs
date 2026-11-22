using System.Globalization;
using System.Text.RegularExpressions;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    private static string AggregateReferenceKey(string expression)
    {
        Match call = AggregateStart.Match(expression);
        if (!call.Success || call.Index != 0)
        {
            return expression;
        }
        int opening = expression.IndexOf('(');
        string argument = expression[(opening + 1)..];
        foreach (Match function in GqlRegex.Matches(MaskQuotedStrings(argument),
            @"(?<!" + GraphGqlIdentifiers.Continuation + @")(?<![.$])[A-Za-z_][A-Za-z_0-9]*(?=\s*\()"))
        {
            argument = argument[..function.Index] + function.Value.ToUpperInvariant() + argument[(function.Index + function.Length)..];
        }
        argument = GqlRegex.Replace(argument, @"^\s*(DISTINCT|ALL)\b", match => match.Value.ToUpperInvariant(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Ignore layout, but retain quoted contents and the exact case of
        // bindings/properties. Function keywords are case insensitive.
        argument = GqlRegex.Replace(argument, "'(?:\\\\.|''|[^'])*'|\"(?:\\\\.|\"\"|[^\"])*\"|`(?:``|[^`])*`|\\s+",
            match => Char.IsWhiteSpace(match.Value[0]) ? "" : match.Value);
        return expression[..opening].Trim().ToUpperInvariant() + "(" + argument;
    }

    private static IEnumerable<string> AggregateBindings(GraphPatternAggregation plan) =>
        plan.GroupBy.SelectMany(key => key.Binding is { } binding ? [binding] : key.Expression!.Bindings)
            .Concat(plan.Aggregates.SelectMany(value => value.ValueBinding is { } binding ? [binding] :
                value.Expression?.Bindings ?? []));

    private static GraphPatternAggregation? ParseAggregateReturn(string source, IReadOnlySet<string>? rowScope = null)
    {
        Match returned = GqlRegex.Match(source, @"^RETURN\s+(?:(?:DISTINCT|ALL)\s+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!returned.Success)
        {
            return null;
        }
        string body = source[returned.Length..].Trim().TrimEnd(';').TrimEnd();
        string masked = MaskQuotedStrings(body);
        var boundaries = new List<(string Name, int Start, int End)>();
        int depth = 0;
        var candidates = GqlRegex.Matches(masked, @"(?<=\s)(GROUP\s+BY|ORDER\s+BY|LIMIT|OFFSET|SKIP)(?=\s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int scanned = 0;
        foreach (Match match in candidates)
        {
            for (; scanned < match.Index; scanned++)
            {
                if (masked[scanned] is '(' or '[' or '{')
                {
                    depth++;
                }
                if (masked[scanned] is ')' or ']' or '}')
                {
                    depth--;
                }
            }
            if (depth == 0)
            {
                boundaries.Add((GqlRegex.Replace(match.Value.ToUpperInvariant(), @"\s+", " "), match.Index, match.Index + match.Length));
            }
        }
        string items = body[..(boundaries.Count == 0 ? body.Length : boundaries[0].Start)].Trim();
        var aggregates = new List<GraphPatternAggregate>();
        var groups = new List<GraphPatternGroupingKey>();
        var sourceAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var aggregateAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = SplitProjectionItems(items).Select(item =>
        {
            Match alias = GraphGqlIdentifiers.ProjectionAlias(item);
            string expression = (alias.Success ? item[..alias.Index] : item).Trim();
            string name = alias.Success ? GraphGqlIdentifiers.DecodeAlias(alias) : expression;
            Match call = GqlRegex.Match(expression, @"^(?<function>COUNT|SUM|MIN|MAX|AVG|COLLECT|COLLECT_LIST|STDEV|STDEVP|STDDEV_SAMP|STDDEV_POP|STDEV_SAMP|STDEV_POP|PERCENTILECONT|PERCENTILEDISC|PERCENTILE_CONT|PERCENTILE_DISC)\s*\((?<value>.*)\)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
            return (Expression: expression, Alias: name, Call: call);
        }).ToArray();
        if (!parts.Any(part => part.Call.Success))
        {
            return null;
        }
        foreach (var part in parts)
        {
            sourceAliases.TryAdd(part.Expression, part.Alias);
            if (!part.Call.Success)
            {
                groups.Add(rowScope is null && GraphGqlIdentifiers.Whole.IsMatch(part.Expression) &&
                    part.Expression.ToUpperInvariant() is not ("NULL" or "TRUE" or "FALSE")
                    ? new(part.Alias, part.Expression)
                    : new(part.Alias, expression: ParseExpression(part.Expression, rowScope)));
                continue;
            }
            aggregateAliases.TryAdd(AggregateReferenceKey(part.Expression), part.Alias);
            string argument = part.Call.Groups["value"].Value.Trim();
            Match qualifier = GqlRegex.Match(argument, @"^(?<distinct>DISTINCT|ALL)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool distinct = qualifier.Success && qualifier.Value.Trim().Equals("DISTINCT", StringComparison.OrdinalIgnoreCase);
            if (qualifier.Success)
            {
                argument = argument[qualifier.Length..].Trim();
            }
            GraphPatternAggregateFunction function = part.Call.Groups["function"].Value.ToUpperInvariant() switch
            {
                "COUNT" => GraphPatternAggregateFunction.Count,
                "SUM" => GraphPatternAggregateFunction.Sum,
                "MIN" => GraphPatternAggregateFunction.Minimum,
                "MAX" => GraphPatternAggregateFunction.Maximum,
                "AVG" => GraphPatternAggregateFunction.Average,
                "STDDEV_SAMP" or "STDEV" or "STDEV_SAMP" => GraphPatternAggregateFunction.StandardDeviationSample,
                "STDDEV_POP" or "STDEVP" or "STDEV_POP" => GraphPatternAggregateFunction.StandardDeviationPopulation,
                "PERCENTILECONT" or "PERCENTILE_CONT" => GraphPatternAggregateFunction.PercentileContinuous,
                "PERCENTILEDISC" or "PERCENTILE_DISC" => GraphPatternAggregateFunction.PercentileDiscrete,
                _ => GraphPatternAggregateFunction.Collect
            };
            decimal? percentile = null;
            if (function is GraphPatternAggregateFunction.PercentileContinuous or GraphPatternAggregateFunction.PercentileDiscrete)
            {
                string[] arguments = SplitProjectionItems(argument).ToArray();
                if (arguments.Length != 2)
                {
                    throw Invalid("A percentile aggregate requires a scalar input and an independent numeric percentile.");
                }
                GraphScalarExpression independent = ParseExpression(arguments[1], rowScope);
                // Check the complete tree, including unchosen CASE branches.
                // Folding must not capture a row binding or volatile function.
                if (!IsConstantExpression(independent))
                {
                    throw Invalid("The percentile requires a deterministic expression without external variable references.");
                }
                QueryValue value = EntityForge.Db.Graph.GraphScalarEvaluator.Evaluate(independent,
                    (_, _) => throw Invalid("The percentile cannot depend on row bindings."));
                if (value is not LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } literal ||
                    !Decimal.TryParse(literal.LexicalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal position))
                {
                    throw Invalid("The percentile expression must produce a finite numeric value.");
                }
                argument = arguments[0].Trim();
                percentile = position;
            }
            bool binding = rowScope is null && GraphGqlIdentifiers.Whole.IsMatch(argument) &&
                argument.ToUpperInvariant() is not ("NULL" or "TRUE" or "FALSE");
            aggregates.Add(new(function, part.Alias, binding ? argument : null, distinct: distinct,
                expression: argument == "*" || binding ? null : ParseExpression(argument, rowScope), percentile: percentile));
        }
        var clauses = new Dictionary<string, string>(StringComparer.Ordinal);
        int phase = 0;
        for (int i = 0; i < boundaries.Count; i++)
        {
            var boundary = boundaries[i];
            int nextPhase = boundary.Name == "GROUP BY" ? 0 : boundary.Name == "ORDER BY" ? 1 : 2;
            string key = boundary.Name == "SKIP" ? "OFFSET" : boundary.Name;
            if (nextPhase < phase || !clauses.TryAdd(key, body[boundary.End..(i + 1 < boundaries.Count ? boundaries[i + 1].Start : body.Length)].Trim()))
            {
                throw Invalid("Aggregate clauses must be unique and follow GROUP BY, ORDER BY, then paging.");
            }
            phase = nextPhase;
        }
        if (clauses.TryGetValue("GROUP BY", out string? explicitGroups))
        {
            var projectedKeys = groups.Select(group => group.Alias).ToHashSet(StringComparer.Ordinal);
            string[] keys = explicitGroups.Trim() == "()" ? [] :
                SplitProjectionItems(explicitGroups).Select(key => key.Trim())
                    .Select(key => projectedKeys.Contains(key) ? key : sourceAliases.GetValueOrDefault(key, key)).ToArray();
            if (keys.Length != groups.Count || !keys.ToHashSet(StringComparer.Ordinal).SetEquals(groups.Select(group => group.Alias)))
            {
                throw Invalid("GROUP BY must contain exactly the non-aggregate projected keys.");
            }
        }
        var order = new List<GraphPatternAggregateOrder>();
        if (clauses.TryGetValue("ORDER BY", out string? orderText))
        {
            foreach (string term in SplitProjectionItems(orderText))
            {
                Match direction = GqlRegex.Match(term, @"\s+(?<direction>ASC|DESC)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                string key = (direction.Success ? term[..direction.Index] : term).Trim();
                order.Add(new(sourceAliases.GetValueOrDefault(key, aggregateAliases.GetValueOrDefault(AggregateReferenceKey(key), key)),
                    direction.Groups["direction"].Value.Equals("DESC", StringComparison.OrdinalIgnoreCase)
                        ? GraphPatternSortDirection.Descending : GraphPatternSortDirection.Ascending));
            }
        }
        int Page(string name, int defaultValue) => !clauses.TryGetValue(name, out string? text) ? defaultValue :
            Int32.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number :
            throw Invalid($"Aggregate {name} requires a non-negative integer.");
        return new(aggregates, groups, order, Page("LIMIT", MaximumAggregateInputRows), Page("OFFSET", 0), MaximumAggregateInputRows);
    }
}
