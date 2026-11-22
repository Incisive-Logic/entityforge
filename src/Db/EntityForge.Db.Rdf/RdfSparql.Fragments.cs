using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Rdf;

// A leaf is a native pattern result, not a locally completed SELECT. Global
// optional/negative joins, aggregates, ordering and windows still run once.
internal sealed record RdfBindingFragment(int Pattern, Dictionary<string, RdfTerm> Bindings,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RdfTerm? OwnerSubject = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    int? Occurrence = null);

public static partial class RdfSparql
{
    private const int CompleteSubjectFragment = -1;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Pattern[]> FragmentPlans = new(StringComparer.Ordinal);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ParsedSelectQuery, QueryProjectionColumns<string>> FragmentColumns = new();

    internal static bool SupportsBindingFragments(string query) => FragmentPatterns(query) is not null;

    private static Pattern[]? FragmentPatterns(string query)
    {
        if (UsesService(query) || UsesValueGraphPatterns(query) || ParseDataset(query) is not null)
        {
            return null;
        }
        if (FragmentPlans.TryGetValue(query, out var cached))
        {
            return cached;
        }

        Group body;
        FilterExpression? having = null;
        switch (QueryForm(query))
        {
            case RdfSparqlQueryForm.Select:
                var select = GetSelectPlan(query);
                if (select.Modifiers.TrailingValues is not null)
                {
                    return null;
                }
                body = select.Body;
                having = select.Modifiers.Having;
                break;
            case RdfSparqlQueryForm.Ask:
            case RdfSparqlQueryForm.Construct:
                var (text, prefixes) = ParseProlog(query);
                var shape = ParseGraphQueryShape(text, prefixes, QueryForm(query) == RdfSparqlQueryForm.Construct);
                body = ParseQueryBody(shape.Body, query, prefixes);
                var options = ParseModifiers(shape.Tail, prefixes);
                if (options.TrailingValues is not null)
                {
                    return null;
                }
                having = options.Having;
                break;
            default:
                return null; // DESCRIBE reads additional facts beyond its pattern leaves.
        }
        var patterns = new List<Pattern>();
        bool supported = true;
        void Filter(FilterExpression? filter)
        {
            switch (filter)
            {
                case ExistsExpression e:
                    Visit(e.Group);
                    break;
                case BinaryBooleanExpression b:
                    Filter(b.Left);
                    Filter(b.Right);
                    break;
                case NotExpression n:
                    Filter(n.Operand);
                    break;
            }
        }
        void Visit(Group group)
        {
            foreach (var element in group.Elements)
            {
                switch (element)
                {
                    case PatternElement p:
                        if (p.Pattern.Path is not null)
                        {
                            supported = false;
                        }
                        else if (!patterns.Contains(p.Pattern))
                        {
                            patterns.Add(p.Pattern);
                        }

                        break;
                    case NamedGraphElement:
                        supported = false;
                        break;
                    case NestedGroupElement n:
                        Visit(n.Group);
                        break;
                    case OptionalElement o:
                        Visit(o.Group);
                        break;
                    case MinusElement m:
                        Visit(m.Group);
                        break;
                    case UnionElement u:
                        foreach (var branch in u.Groups)
                        {
                            Visit(branch);
                        }

                        break;
                    case SubqueryElement s:
                        Visit(s.Query.Body);
                        Filter(s.Query.Modifiers.Having);
                        break;
                }
            }

            foreach (var filter in group.Filters)
            {
                Filter(filter);
            }
        }
        Visit(body);
        Filter(having);
        if (!supported || patterns.Count > MaximumPatterns)
        {
            return null;
        }

        var result = patterns.ToArray();
        if (query.Length <= MaximumCachedSelectQueryCharacters && FragmentPlans.Count < MaximumCachedSelectPlans)
        {
            return FragmentPlans.GetOrAdd(query, result);
        }

        return result;
    }

    internal static RdfBindingFragment[] CaptureBindingFragments(RdfDatabaseEngine database,
        RdfSnapshotLease snapshot, string query, int maximum, CancellationToken token)
    {
        var patterns = FragmentPatterns(query) ?? throw new NotSupportedException("Unsupported RDF fragment plan.");
        // The enclosing query owns the snapshot; this view does not dispose it.
        var view = new RdfSparqlReadView(database, snapshot, cancellation: token);
        if (LocalFragmentBody(query) is { } local)
        {
            var rows = EvaluateGroup(view, local, [new(StringComparer.Ordinal)]);
            if (rows.Count > maximum)
            {
                throw new RdfSparqlWorkLimitException("RDF subject fragment exceeds its row bound.");
            }

            var columns = LocalFragmentColumns(query);
            var subject = ((PatternElement)local.Elements[0]).Pattern.Subject;
            return rows.Select((row, ordinal) =>
            {
                // Full live rows already carry exact fact/ownership evidence.
                // Only discarded columns need separate occurrence/owner evidence.
                if (row.Keys.All(columns.Contains))
                {
                    return new RdfBindingFragment(CompleteSubjectFragment, row);
                }

                var projected = columns.Project(row);
                return new RdfBindingFragment(CompleteSubjectFragment, projected,
                    Resolve(subject, projected) is null ? Resolve(subject, row) ?? throw new InvalidDataException("Missing local subject.") : null,
                    ordinal);
            }).ToArray();
        }
        var results = new List<RdfBindingFragment>();
        for (int index = 0; index < patterns.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            foreach (var row in Match(view, patterns[index], [new(StringComparer.Ordinal)]))
            {
                results.Add(new(index, row));
                if (results.Count > maximum)
                {
                    throw new RdfSparqlWorkLimitException("RDF fragment exchange exceeds its row bound.");
                }
            }
        }
        return results.ToArray();
    }

    internal static Func<RdfBindingFragment, RdfQuad> CreateBindingFragmentValidator(string query)
    {
        var patterns = FragmentPatterns(query) ?? throw new InvalidDataException("Unsupported RDF fragment plan.");
        var local = LocalFragmentBody(query);
        // Capture immutable interpretation once per execution. The bounded global
        // caches may be full; correctness and per-row cost must not depend on a hit.
        return fragment => ValidateBindingFragment(patterns, local, fragment);
    }

    internal static RdfQuad ValidateBindingFragment(string query, RdfBindingFragment fragment) =>
        CreateBindingFragmentValidator(query)(fragment);

    // Local filters and star joins already ran under the owner's protected cut.
    // The home validates interpretation, subject ownership and row occurrence;
    // discarded local columns cannot participate in a later global operator.
    internal static Func<RdfBindingFragment, RdfTerm> CreateBindingFragmentOwnerValidator(string query)
    {
        var patterns = FragmentPatterns(query) ?? throw new InvalidDataException("Unsupported fragment plan.");
        var local = LocalFragmentBody(query);
        var columns = local is null ? null : LocalFragmentColumns(query);
        return fragment =>
        {
            if (local is null)
            {
                if (fragment.OwnerSubject is not null || fragment.Occurrence is not null)
                {
                    throw new InvalidDataException("Unexpected local completion evidence.");
                }

                return ValidateBindingFragment(patterns, null, fragment).Subject;
            }
            if (fragment.Occurrence is null && fragment.OwnerSubject is null)
            {
                return ValidateBindingFragment(patterns, local, fragment).Subject;
            }

            var subject = ((PatternElement)local.Elements[0]).Pattern.Subject;
            var owner = fragment.OwnerSubject ?? (fragment.Bindings is null ? null : Resolve(subject, fragment.Bindings));
            if (fragment.Pattern != CompleteSubjectFragment || owner is null ||
                owner is RdfLiteral || fragment.Occurrence is null or < 0 || fragment.Occurrence >= MaximumResults ||
                fragment.Bindings is null || fragment.Bindings.Keys.Any(key => !columns!.Contains(key)))
            {
                throw new InvalidDataException("Invalid projected RDF row.");
            }

            RdfTermValidation.Validate(owner, subject: true);
            foreach (var value in fragment.Bindings.Values)
            {
                RdfTermValidation.Validate(value, subject: false);
            }

            var check = new Dictionary<string, RdfTerm>(StringComparer.Ordinal);
            if (!Bind(subject, owner, check) || check.Any(pair =>
                fragment.Bindings.TryGetValue(pair.Key, out var value) && value != pair.Value))
            {
                throw new InvalidDataException("RDF row owner association differs.");
            }

            return owner;
        };
    }

    private static QueryProjectionColumns<string> LocalFragmentColumns(string query)
    {
        var plan = GetSelectPlan(query);
        return FragmentColumns.GetValue(plan, BuildLocalFragmentColumns);
    }

    private static QueryProjectionColumns<string> BuildLocalFragmentColumns(ParsedSelectQuery plan)
    {
        if (plan.Projection.SelectAll || plan.Projection.Aggregates.Any(aggregate => aggregate.Distinct && aggregate.Value is null))
        {
            return new(null, StringComparer.Ordinal);
        }

        var columns = plan.Projection.OutputVariables.ToHashSet(StringComparer.Ordinal);
        columns.UnionWith(plan.Projection.Aggregates.Select(a => a.Argument).OfType<string>());
        foreach (ValueExpression expression in plan.Projection.Aggregates.Select(aggregate => aggregate.Value).OfType<ValueExpression>()
                     .Concat(plan.Projection.Computed.Select(binding => binding.Value)))
        {
            columns.UnionWith(ValueVariables(expression));
        }
        columns.UnionWith(plan.Modifiers.GroupVariables);
        // HAVING is applied globally after aggregation. Keep its input variables
        // as well, including aliases; do not approximate expression dependencies.
        void Term(TermPattern term)
        {
            if (term.Variable is { } variable)
            {
                columns.Add(variable);
            }

            if (term.Quoted is { } quoted)
            {
                Term(quoted.Subject);
                Term(quoted.Predicate);
                Term(quoted.Object);
            }
        }
        void Value(ValueExpression value)
        {
            columns.UnionWith(ValueVariables(value));
            if (value is TermValueExpression term)
            {
                Term(term.Term);
            }
            else if (value is ValueFunctionExpression function)
            {
                Value(function.Argument);
            }
        }
        void Filter(FilterExpression? filter)
        {
            switch (filter)
            {
                case ComparisonExpression comparison:
                    Value(comparison.Left);
                    Value(comparison.Right);
                    break;
                case EffectiveBooleanExpression boolean:
                    Value(boolean.Value);
                    break;
                case BinaryBooleanExpression boolean:
                    Filter(boolean.Left);
                    Filter(boolean.Right);
                    break;
                case NotExpression not:
                    Filter(not.Operand);
                    break;
                case FunctionExpression function:
                    Value(function.First);
                    if (function.Second is { } second)
                    {
                        Value(second);
                    }

                    if (function.Third is { } third)
                    {
                        Value(third);
                    }

                    break;
                case InExpression @in:
                    Value(@in.Operand);
                    foreach (var value in @in.Values)
                    {
                        Value(value);
                    }

                    break;
                case ExistsExpression:
                    throw new InvalidDataException("Non-local HAVING cannot use projected local rows.");
            }
        }
        foreach (OrderCondition condition in plan.Modifiers.Order)
        {
            Value(condition.Value);
        }
        Filter(plan.Modifiers.Having);
        return new(columns, StringComparer.Ordinal);
    }

    private static RdfQuad ValidateBindingFragment(Pattern[] patterns, Group? local, RdfBindingFragment fragment)
    {
        if (fragment.Bindings is null || (local is not null ? fragment.Pattern != CompleteSubjectFragment : (uint)fragment.Pattern >= patterns.Length))
        {
            throw new InvalidDataException("Invalid RDF fragment ordinal.");
        }

        var row = fragment.Bindings;
        var exact = new Dictionary<string, RdfTerm>(StringComparer.Ordinal);
        RdfQuad? first = null;
        foreach (var p in local is not null ? patterns : [patterns[fragment.Pattern]])
        {
            var subject = Resolve(p.Subject, row);
            var predicate = Resolve(p.Predicate!, row);
            var obj = Resolve(p.Object, row);
            var graph = p.Graph is null ? null : Resolve(p.Graph, row);
            if (subject is null || predicate is not RdfIri iri || obj is null || p.Graph is not null && graph is null ||
                !Bind(p.Subject, subject, exact) || !Bind(p.Predicate!, iri, exact) || !Bind(p.Object, obj, exact) ||
                p.Graph is not null && !Bind(p.Graph, graph!, exact))
            {
                throw new InvalidDataException("Incomplete RDF binding.");
            }

            var quad = new RdfQuad(subject, iri, obj, graph).Validate();
            first ??= quad;
        }
        if (exact.Count != row.Count || exact.Any(pair => !row.TryGetValue(pair.Key, out var value) || value != pair.Value))
        {
            throw new InvalidDataException("RDF binding differs from its pattern.");
        }

        return first ?? throw new InvalidDataException("Missing RDF pattern.");
    }

    // Every matched fact has the same subject: the entire star and its scalar
    // predicates are owner-local. Aggregation/projection/windowing remain global.
    private static Group? LocalFragmentBody(string query)
    {
        // Bind the query clock at the coordinator, never independently per shard.
        if (UsesValueGraphPatterns(query) || UsesQueryClock(query) || UsesSolutionBlankNodes(query) || ParseDataset(query) is not null)
        {
            return null;
        }
        if (QueryForm(query) != RdfSparqlQueryForm.Select)
        {
            return null;
        }

        var plan = GetSelectPlan(query);
        var body = plan.Body;
        if (plan.Modifiers.TrailingValues is not null)
        {
            return null;
        }
        if (body.Elements.Count == 0 || body.Elements.Any(e => e is not PatternElement { Pattern.Path: null }))
        {
            return null;
        }

        var first = ((PatternElement)body.Elements[0]).Pattern.Subject;
        if (first.Quoted is not null || body.Elements.Cast<PatternElement>().Any(p => p.Pattern.Subject != first))
        {
            return null;
        }

        bool Local(FilterExpression? filter) => filter switch
        {
            ExistsExpression => false,
            BinaryBooleanExpression b => Local(b.Left) && Local(b.Right),
            NotExpression n => Local(n.Operand),
            _ => true
        };
        return body.Filters.All(Local) && Local(plan.Modifiers.Having) ? body : null;
    }

    internal static RdfSparqlQueryResult ExecuteBindingFragments(string query,
        IReadOnlyList<RdfBindingFragment> fragments, CancellationToken token)
    {
        var patterns = FragmentPatterns(query) ?? throw new NotSupportedException("Unsupported RDF fragment plan.");
        var rows = patterns.ToDictionary(p => p, _ => new List<Dictionary<string, RdfTerm>>());
        var localRows = LocalFragmentBody(query) is not null ? new List<Dictionary<string, RdfTerm>>() : null;
        var validate = CreateBindingFragmentOwnerValidator(query);
        foreach (var fragment in fragments)
        {
            token.ThrowIfCancellationRequested();
            _ = validate(fragment);
            AddBounded(localRows ?? rows[patterns[fragment.Pattern]], fragment.Bindings);
        }
        using var view = new RdfSparqlReadView(null, null, [], token) { FragmentRows = localRows is null ? rows : null, RootRows = localRows };
        return ExecuteView(view, query);
    }

    private static List<Dictionary<string, RdfTerm>> JoinFragmentRows(RdfSparqlReadView view,
        Pattern pattern, IReadOnlyList<Dictionary<string, RdfTerm>> input)
    {
        var output = new List<Dictionary<string, RdfTerm>>();
        foreach (var left in input)
        {
            foreach (var right in view.FragmentRows![pattern])
            {
                view.CheckCancellation();
                if (right.Any(pair => left.TryGetValue(pair.Key, out var value) && value != pair.Value))
                {
                    continue;
                }

                var joined = new Dictionary<string, RdfTerm>(left, StringComparer.Ordinal);
                foreach (var pair in right)
                {
                    joined[pair.Key] = pair.Value;
                }

                AddBounded(output, joined);
            }
        }

        return output;
    }
}
