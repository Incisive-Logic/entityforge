using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

// A stage carries distinct restrictions for one triple pattern, never a locally
// completed join. The coordinator retains the input bag and its multiplicity.
internal sealed record RdfBindingFrontier(int Pattern, Dictionary<string, RdfTerm>[] Bindings);

public static partial class RdfSparql
{
    internal static bool SupportsBindingFrontier(string query)
    {
        if (UsesService(query) || UsesValueGraphPatterns(query) || QueryForm(query) != RdfSparqlQueryForm.Select || LocalFragmentBody(query) is not null || ParseDataset(query) is not null)
        {
            return false;
        }

        var plan = GetSelectPlan(query);
        bool Basic(Group group) => group.Filters.Count == 0 && group.Elements.All(element => element switch
        {
            PatternElement { Pattern.Path: null } p =>
                new[] { p.Pattern.Subject, p.Pattern.Predicate, p.Pattern.Object, p.Pattern.Graph }
                    .OfType<TermPattern>().All(t => t.Quoted is null),
            NamedGraphElement => false,
            NestedGroupElement nested => Basic(nested.Group),
            _ => false
        });
        return plan.Modifiers.TrailingValues is null && plan.Modifiers.Having is null && Basic(plan.Body);
    }

    internal static RdfBindingFragment[] CaptureBindingFrontier(RdfDatabaseEngine database,
        RdfSnapshotLease snapshot, string query, RdfBindingFrontier frontier, int maximum, CancellationToken token)
    {
        if (!SupportsBindingFrontier(query))
        {
            throw new InvalidDataException("Unsupported RDF frontier interpretation.");
        }

        var patterns = FragmentPatterns(query)!;
        if ((uint)frontier.Pattern >= patterns.Length || frontier.Bindings is null || frontier.Bindings.Length > MaximumResults)
        {
            throw new InvalidDataException("Invalid RDF frontier stage.");
        }

        var pattern = patterns[frontier.Pattern];
        var variables = PatternVariables(pattern).ToHashSet(StringComparer.Ordinal);
        var view = new RdfSparqlReadView(database, snapshot, cancellation: token);
        var result = new List<RdfBindingFragment>();
        var unique = new HashSet<RdfQuad>();
        foreach (var restriction in frontier.Bindings)
        {
            token.ThrowIfCancellationRequested();
            if (restriction is null || restriction.Keys.Any(k => !variables.Contains(k)))
            {
                throw new InvalidDataException("RDF frontier contains an unrelated variable.");
            }

            foreach (var term in restriction.Values)
            {
                RdfTermValidation.Validate(term, subject: false);
            }

            foreach (var row in Match(view, pattern, [restriction]))
            {
                var fragment = new RdfBindingFragment(frontier.Pattern, row);
                var quad = ValidateBindingFragment(patterns, null, fragment);
                if (!unique.Add(quad))
                {
                    continue;
                }

                result.Add(fragment);
                if (result.Count > Math.Min(maximum, MaximumResults))
                {
                    throw new RdfSparqlWorkLimitException("RDF frontier exceeds its row bound.");
                }
            }
        }
        return result.ToArray();
    }

    internal static async ValueTask<RdfSparqlQueryResult> ExecuteBindingFrontierAsync(string query,
        Func<RdfBindingFrontier, CancellationToken, ValueTask<IReadOnlyList<RdfBindingFragment>>> read,
        CancellationToken token)
    {
        if (!SupportsBindingFrontier(query))
        {
            throw new InvalidDataException("Unsupported RDF frontier interpretation.");
        }

        var patterns = FragmentPatterns(query)!;
        var remaining = Enumerable.Range(0, patterns.Length).ToList();
        var bound = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<Dictionary<string, RdfTerm>> { new(StringComparer.Ordinal) };
        while (remaining.Count != 0 && rows.Count != 0)
        {
            token.ThrowIfCancellationRequested();
            // Bound joins precede disconnected scans. At the root, exact subject
            // and literal lookups precede broad type/predicate relations.
            int index = remaining.MinBy(i => (
                -PatternVariables(patterns[i]).Count(bound.Contains),
                patterns[i].Subject.Constant is null ? 1 : 0,
                patterns[i].Object.Constant is RdfLiteral ? 0 : 1,
                -PatternBoundTermCount(patterns[i]), i));
            var variables = PatternVariables(patterns[index]).ToHashSet(StringComparer.Ordinal);
            var restrictions = new List<Dictionary<string, RdfTerm>>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var restriction = row.Where(p => variables.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                string key = System.Text.Json.JsonSerializer.Serialize(restriction.OrderBy(p => p.Key, StringComparer.Ordinal));
                if (keys.Add(key))
                {
                    restrictions.Add(restriction);
                }
            }
            var fragments = await read(new(index, restrictions.ToArray()), token);
            var stage = new List<Dictionary<string, RdfTerm>>();
            var facts = new HashSet<RdfQuad>();
            foreach (var fragment in fragments)
            {
                token.ThrowIfCancellationRequested();
                if (fragment.Pattern != index)
                {
                    throw new InvalidDataException("RDF frontier response belongs to another stage.");
                }

                var fact = ValidateBindingFragment(patterns, null, fragment);
                if (!facts.Add(fact))
                {
                    throw new InvalidDataException("Duplicate RDF frontier fact.");
                }

                if (!restrictions.Any(r => r.All(p => fragment.Bindings.TryGetValue(p.Key, out var v) && v == p.Value)))
                {
                    throw new InvalidDataException("RDF frontier response is outside its requested bindings.");
                }

                AddBounded(stage, fragment.Bindings);
            }
            using var view = new RdfSparqlReadView(null, null, [], token)
            {
                FragmentRows = new Dictionary<Pattern, List<Dictionary<string, RdfTerm>>> { [patterns[index]] = stage }
            };
            rows = JoinFragmentRows(view, patterns[index], rows);
            bound.UnionWith(variables);
            remaining.Remove(index);
        }
        using var complete = new RdfSparqlReadView(null, null, [], token) { RootRows = rows };
        return ExecuteView(complete, query);
    }
}
