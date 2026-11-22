using System.Text.RegularExpressions;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Db.Graph;

namespace EntityForge.Db.Gql;

internal sealed record GqlPathBinding(string Alias, string Start, IReadOnlyList<(string Edge, string Target)> Steps,
    string? Selector = null, int Count = 0, bool Groups = false, string? Mode = null)
{
    internal GraphPatternResult Bind(GraphPatternResult result)
    {
        if (result.Matches.Count > GraphGqlQueryCompiler.MaximumAggregateInputRows)
        {
            throw new ArgumentException("A path selection exceeds 10,000 candidate rows.");
        }
        var rows = result.Matches.Select(row =>
        {
            var elements = new List<QueryValue> { row.Bindings[Start] };
            foreach (var step in Steps)
            {
                if (row.Bindings[step.Edge] is PathValue path)
                {
                    elements.AddRange(path.Elements.Skip(1));
                }
                else
                {
                    elements.Add(row.Bindings[step.Edge]);
                    elements.Add(row.Bindings[step.Target]);
                }
            }
            var bindings = new Dictionary<string, QueryValue>(row.Bindings, StringComparer.Ordinal) { [Alias] = new PathValue(elements) };
            return new GraphPatternMatch(bindings, row.Paths);
        }).Where(row => AcceptsMode((PathValue)row.Bindings[Alias])).ToArray();
        if (Selector is null)
        {
            return new(result.Graph, rows);
        }
        string Key(GraphPatternMatch row) => GraphScalarEvaluator.DistinctKey(new ListValue(
            [row.Bindings[Start], row.Bindings[Steps.Count == 0 ? Start : Steps[^1].Target]]));
        IEnumerable<GraphPatternMatch> Selected(IEnumerable<GraphPatternMatch> partition)
        {
            var ordered = partition.OrderBy(row => Selector == "SHORTEST" ? ((PathValue)row.Bindings[Alias]).Elements.Count : 0)
                .ThenBy(row => GraphScalarEvaluator.DistinctKey(row.Bindings[Alias]), StringComparer.Ordinal);
            return Groups ? ordered.GroupBy(row => ((PathValue)row.Bindings[Alias]).Elements.Count).Take(Count).SelectMany(group => group)
                : ordered.Take(Count);
        }
        return new(result.Graph, rows.GroupBy(Key).SelectMany(Selected));
    }

    private bool AcceptsMode(PathValue path)
    {
        if (Mode is null or "WALK")
        {
            return true;
        }
        var identities = Mode == "TRAIL"
            ? path.Elements.OfType<EdgeValue>().Select(edge => edge.Identity).ToArray()
            : path.Elements.OfType<NodeValue>().Select(node => node.Identity).ToArray();
        int length = identities.Length;
        if (Mode == "SIMPLE" && length > 1 && Equals(identities[0], identities[^1]))
        {
            length--;
        }
        return identities.Take(length).Distinct().Count() == length;
    }
}

internal sealed partial class GraphGqlQueryCompiler
{
    [GeneratedRegex(@"^\s*(?<name>" + GraphGqlIdentifiers.Pattern + @")\s*=\s*")]
    private static partial Regex PathAssignment();

    [GeneratedRegex(@"^\s*(?<mode>WALK|TRAIL|ACYCLIC|SIMPLE)(?:\s+PATHS?)?\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitPathMode();

    [GeneratedRegex(@"^\s*(?:(?<all>ALL)\s+SHORTEST|(?<one>ANY)\s+SHORTEST|(?<mode>SHORTEST|ANY)(?:\s+(?<count>[0-9]+))?|(?<every>ALL))(?:\s+(?<pathMode>WALK|TRAIL|ACYCLIC|SIMPLE))?(?:\s+PATHS?)?(?<groups>\s+GROUPS?)?\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CountedPathSelector();

    private static CompiledGraphGqlQuery PathReadPlan(CompiledGraphGqlQuery probe)
    {
        var query = probe.Query;
        string[] names = query.Relationships.SelectMany(edge => new[] { edge.Binding, edge.Target.Binding })
            .Prepend(query.Start.Binding).Distinct(StringComparer.Ordinal).ToArray();
        return new(new BoundedGraphPatternQuery(query.Start, query.Relationships, MaximumAggregateInputRows + 1,
            query.MaximumExpansions, names, filter: query.Filter), probe.SnapshotVersion)
        {
            GroupRelationshipBindings = probe.GroupRelationshipBindings
        };
    }

    private static GqlPathBinding CreatePathBinding(string alias, CompiledGraphGqlQuery plan, IReadOnlySet<string> scope)
    {
        if (scope.Contains(alias) || plan.Query.ReturnedBindings.Contains(alias))
        {
            throw Invalid("A path assignment requires a new variable name.");
        }
        string previous = plan.Query.Start.Binding;
        foreach (var edge in plan.Query.Relationships)
        {
            if (edge.SourceBinding != previous)
            {
                throw Invalid("A named path must be one connected pattern chain.");
            }
            previous = edge.Target.Binding;
        }
        return new(alias, plan.Query.Start.Binding, plan.Query.Relationships.Select(edge => (edge.Binding, edge.Target.Binding)).ToArray());
    }
}
