using System.Text.RegularExpressions;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed partial class GraphGqlQueryCompiler
{
    internal static (string Source, Dictionary<string, string> Names) PrepareImplicitColumns(string source, IReadOnlySet<string>? imports = null)
    {
        source = StripComments(source);
        var clauses = WithClauses(source);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var reserved = GraphGqlIdentifiers.Occurrences.Matches(MaskQuotedStrings(source))
            .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        if (imports is not null)
        {
            reserved.UnionWith(imports);
        }
        var edits = new List<(int Start, int Length, string Text)>();
        for (int index = 0; index < clauses.Count; index++)
        {
            var clause = clauses[index];
            if (clause.Word != "RETURN")
            {
                continue;
            }
            int start = clause.Start + clause.Length;
            int end = index + 1 < clauses.Count ? clauses[index + 1].Start : source.Length;
            string body = source[start..end].Trim().TrimEnd(';').TrimEnd();
            var qualifier = GqlRegex.Match(body, @"^(DISTINCT|ALL)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            string prefix = qualifier.Success ? qualifier.Value : String.Empty;
            body = body[prefix.Length..];
            var terms = new List<string>();
            var local = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string item in SplitProjectionItems(body))
            {
                string expression = item.Trim();
                if (expression == "*" || GraphGqlIdentifiers.Whole.IsMatch(expression) ||
                    GraphGqlIdentifiers.ProjectionAlias(expression).Success)
                {
                    terms.Add(expression);
                    continue;
                }
                if (!aliases.TryGetValue(expression, out string? alias))
                {
                    alias = "__resultColumn" + aliases.Count;
                    while (!reserved.Add(alias))
                    {
                        alias += "_";
                    }
                    aliases.Add(expression, alias);
                    names.Add(alias, expression);
                }
                local[expression] = alias;
                terms.Add(expression + " AS " + alias);
            }
            if (local.Count == 0)
            {
                continue;
            }
            edits.Add((start, end - start, " " + prefix + String.Join(",", terms) + " "));
            for (int modifier = index + 1; modifier < clauses.Count && clauses[modifier].Word is "ORDER BY" or "LIMIT" or "SKIP" or "OFFSET"; modifier++)
            {
                var ordering = clauses[modifier];
                if (ordering.Word != "ORDER BY")
                {
                    continue;
                }
                int orderStart = ordering.Start + ordering.Length;
                int orderEnd = modifier + 1 < clauses.Count ? clauses[modifier + 1].Start : source.Length;
                string order = source[orderStart..orderEnd].Trim().TrimEnd(';').TrimEnd();
                var rewritten = SplitProjectionItems(order).Select(item =>
                {
                    var direction = GqlRegex.Match(item, @"\s+(ASC|DESC)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    string expression = (direction.Success ? item[..direction.Index] : item).Trim();
                    return local.GetValueOrDefault(expression, expression) + (direction.Success ? direction.Value : String.Empty);
                });
                edits.Add((orderStart, orderEnd - orderStart, " " + String.Join(",", rewritten) + " "));
            }
        }
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            source = source[..edit.Start] + edit.Text + source[(edit.Start + edit.Length)..];
        }
        return (source, names);
    }
}

internal sealed partial record CompiledGraphGqlQuery
{
    internal QueryResultEnvelope ApplyOutputNames(QueryResultEnvelope envelope) => RenameOutput(envelope, OutputNames);

    internal static QueryResultEnvelope RenameOutput(QueryResultEnvelope envelope, IReadOnlyDictionary<string, string>? outputNames)
    {
        if (outputNames is not { Count: > 0 } names)
        {
            return envelope;
        }
        IEnumerable<KeyValuePair<string, QueryValue>> Rename(IReadOnlyDictionary<string, QueryValue> values) =>
            values.Select(pair => new KeyValuePair<string, QueryValue>(names.GetValueOrDefault(pair.Key, pair.Key), pair.Value));
        QueryResult result = envelope.Result switch
        {
            GraphPatternResult graph => new GraphPatternResult(graph.Graph, graph.Matches.Select(row => new GraphPatternMatch(Rename(row.Bindings), row.Paths))),
            RecordResult records => new RecordResult(records.Fields.Select(field => new RecordField(names.GetValueOrDefault(field.Name, field.Name))),
                records.Rows.Select(row => new RecordRow(Rename(row.Values)))),
            _ => envelope.Result
        };
        return new(envelope.QueryId, result, envelope.CorrelationId, envelope.Evidence, envelope.SemanticModels, envelope.Diagnostics);
    }
}
