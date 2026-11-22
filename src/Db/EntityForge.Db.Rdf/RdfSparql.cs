using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Numerics;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Rdf;

/// <summary>Parses and evaluates supported SPARQL query forms over RDF data under explicit execution limits.</summary>
public static partial class RdfSparql
{
    /// <summary>Maximum triple patterns accepted by the bounded adapter.</summary>
    public const int MaximumPatterns = 16;

    /// <summary>Maximum intermediate and final result rows accepted by the bounded adapter.</summary>
    public const int MaximumResults = 10_000;

    /// <summary>Maximum lexical tokens accepted in one query.</summary>
    public const int MaximumTokens = 512;

    /// <summary>Maximum entries accepted by one VALUES clause.</summary>
    public const int MaximumValues = 256;

    /// <summary>Maximum nesting depth accepted in a FILTER expression.</summary>
    public const int MaximumExpressionDepth = 16;

    /// <summary>Maximum nesting depth accepted for graph groups.</summary>
    public const int MaximumGroupDepth = 16;

    /// <summary>Maximum predicate occurrences in one property-path expression; not a traversal depth limit.</summary>
    public const int MaximumPathSteps = 16;

    /// <summary>Maximum predicate expansions and examined facts per property-path pattern evaluation.</summary>
    public const int MaximumPathExpansions = 100_000;

    /// <summary>Maximum prefix declarations accepted in one query prolog.</summary>
    public const int MaximumPrefixes = 32;
    private const int MaximumCachedSelectPlans = 256;
    private const int MaximumCachedSelectQueryCharacters = 16_384;
    private static readonly ConcurrentDictionary<string, ParsedSelectQuery> SelectPlans =
        new(StringComparer.Ordinal);

    /// <summary>Describes the precise bounded SPARQL surface implemented by this version.</summary>
    public static RdfSparqlCapabilities Capabilities
    {
        get;
    }
    = new(
        "preview", ["SELECT", "ASK", "CONSTRUCT", "DESCRIBE (direct subject)"],
        ["INSERT DATA", "DELETE DATA", "DELETE/INSERT WHERE", "DELETE WHERE", "CREATE GRAPH", "CLEAR DEFAULT", "CLEAR NAMED", "CLEAR ALL", "CLEAR GRAPH", "DROP DEFAULT", "DROP GRAPH", "DROP NAMED", "DROP ALL", "ADD", "COPY", "MOVE", "LOAD"],
        ["PREFIX", "BASE", "FROM and FROM NAMED over stored graphs",
            "prefixed names",
            "Unicode variable and prefixed names with local-name escapes",
            "rdf:type shorthand",
            "basic-graph-pattern",
            "nested and empty groups",
            "property paths (sequence, alternative, inverse, negated predicate sets, ?, *, +) with explicit work bounds",
            "GRAPH",
            "SERVICE and SERVICE SILENT with configured HTTP endpoint IRIs",
            "OPTIONAL",
            "UNION",
            "VALUES",
            "trailing VALUES after grouping and HAVING",
            "GRAPH active scopes including empty groups and subqueries",
            "CONSTRUCT WHERE shorthand",
            "WITH, USING and USING NAMED modification datasets",
            "atomic sequences of up to 16 supported updates with sequential visibility",
            "empty VALUES tables and zero-column rows",
            "fresh labelled blank nodes in CONSTRUCT and INSERT templates",
            "blank-node WHERE patterns, anonymous nodes, property lists and RDF collections",
            "GROUP BY expressions and aliases",
            "ASK, CONSTRUCT and DESCRIBE solution modifiers",
            "DESCRIBE without WHERE and wildcard targets",
            "BIND and SELECT expressions (arithmetic, comparison, boolean, supported functions)",
            "FILTER boolean/comparison",
            "BOUND",
            "isIRI",
            "isBlank",
            "isLiteral",
            "STRSTARTS",
            "CONTAINS",
            "REGEX",
            "LANG",
            "LANGMATCHES",
            "DATATYPE",
            "STR",
            "IRI / URI (absolute IRIs and relative references with BASE)",
            "STRLEN",
            "UCASE",
            "LCASE",
            "ABS",
            "CEIL",
            "FLOOR",
            "ROUND",
            "ENCODE_FOR_URI",
            "MD5",
            "SHA1",
            "SHA256",
            "SHA384",
            "SHA512",
            "STRENDS",
            "isURI",
            "isNumeric",
            "UUID",
            "STRUUID",
            "NOW",
            "BNODE",
            "RAND",
            "sameTerm",
            "IN",
            "NOT IN",
            "EXISTS",
            "NOT EXISTS",
            "MINUS",
            "subqueries",
            "SELECT *",
            "COUNT",
            "SUM",
            "MIN",
            "MAX",
            "AVG",
            "SAMPLE",
            "GROUP_CONCAT",
            "IF",
            "COALESCE",
            "CONCAT",
            "SUBSTR",
            "REPLACE",
            "YEAR", "MONTH", "DAY", "HOURS", "MINUTES", "SECONDS", "TIMEZONE", "TZ",
            "xsd:string", "xsd:boolean", "xsd:integer", "xsd:decimal", "xsd:float", "xsd:double", "xsd:dateTime",
            "xsd:long", "xsd:int", "xsd:short", "xsd:byte", "xsd:unsignedLong", "xsd:unsignedInt", "xsd:unsignedShort", "xsd:unsignedByte",
            "xsd:positiveInteger", "xsd:negativeInteger", "xsd:nonPositiveInteger", "xsd:nonNegativeInteger",
            "STRBEFORE",
            "STRAFTER",
            "STRDT",
            "STRLANG",
            "GROUP BY",
            "HAVING",
            "ORDER BY multiple expressions and aggregates",
            "triple-term-pattern",
            "DISTINCT",
            "REDUCED",
            "LIMIT",
            "OFFSET"],
        MaximumPatterns, MaximumResults, MaximumTokens, MaximumValues, MaximumExpressionDepth,
        MaximumGroupDepth, MaximumPrefixes, MaximumPathSteps, MaximumPathExpansions);

    /// <summary>Executes the supported bounded SELECT surface.</summary>
    internal static IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> Execute(
        IRdfDatabase databaseRoot, string query)
    {
        using RdfSparqlReadView database = CreateReadView(databaseRoot);
        return Execute(database, query);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> Execute(
        RdfSparqlReadView database, string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        RequireSynchronousQuery(query);
        ParsedSelectQuery parsed = GetSelectPlan(query);
        if (parsed.Dataset is { } dataset)
        {
            using var selected = DatasetView(database, dataset);
            return Execute(selected, dataset.Query);
        }
        if (UsesQueryClock(query))
        {
            parsed = new QueryFunctionBindings().Plan(parsed);
        }
        return ExecuteSelect(database, parsed);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> ExecuteSelect(RdfSparqlReadView database, ParsedSelectQuery parsed)
    {
        SelectProjection projection = parsed.Projection;
        Group body = parsed.Body;
        Modifiers modifiers = parsed.Modifiers;
        if (modifiers.Limit == 0)
        {
            return [];
        }
        bool distinct = parsed.Distinct;
        List<Dictionary<string, RdfTerm>> rows = [];
        bool orderedProjection = modifiers.TrailingValues is null && database.FragmentRows is null && database.RootRows is null && !distinct &&
            modifiers.Having is null &&
            TryExecuteSimpleOrderedProjection(database, body, projection, modifiers, out rows);
        bool directProjection = modifiers.TrailingValues is null && database.FragmentRows is null && database.RootRows is null && !orderedProjection && !distinct && modifiers.Having is null &&
            modifiers.Order.Count == 0 && modifiers.GroupVariables.Count == 0 &&
            (TryExecuteSimpleSinglePatternProjection(database, body, projection, out rows) ||
             TryExecuteSimpleTwoPatternProjection(database, body, projection, out rows));
        if (database.RootRows is { } completeRows)
        {
            rows = ApplyAggregation(completeRows, projection, modifiers.GroupVariables, database);
        }
        else if (!orderedProjection && !directProjection &&
            (database.FragmentRows is not null || !TryExecuteSimpleCountAggregation(database, body, projection, modifiers.GroupVariables, out rows)))
        {
            rows = EvaluateGroup(database, body,
                [new Dictionary<string, RdfTerm>(StringComparer.Ordinal)]);
            rows = ApplyAggregation(rows, projection, modifiers.GroupVariables, database);
        }
        if (!orderedProjection && modifiers.Having is not null)
        {
            rows = rows.Where(row => Evaluate(database, modifiers.Having, HavingRow(row, projection)) == FilterResult.True).ToList();
        }

        if (modifiers.TrailingValues is { } values)
        {
            rows = EvaluateValues(values, rows, database.Cancellation);
        }
        rows = ApplyComputedProjection(rows, projection, database);
        bool orderingAppliedWindow = false;
        if (!orderedProjection && modifiers.Order.Count != 0)
        {
            if (!distinct && checked(modifiers.Offset + modifiers.Limit) < rows.Count / 2)
            {
                rows = SelectOrderedRows(rows, modifiers, database);
                orderingAppliedWindow = true;
            }
            else
            {
                SortRows(rows, modifiers, database);
            }
        }

        IEnumerable<IReadOnlyDictionary<string, RdfTerm>> projected = rows.Select(row =>
            ProjectRow(row, projection));
        if (distinct)
        {
            projected = DistinctRows(projected);
        }

        return (orderedProjection || orderingAppliedWindow
            ? projected : projected.Skip(modifiers.Offset).Take(modifiers.Limit)).ToArray();
    }

    private static bool TryExecuteSimpleSinglePatternProjection(RdfSparqlReadView database, Group body,
        SelectProjection projection, out List<Dictionary<string, RdfTerm>> rows)
    {
        var directRows = new List<Dictionary<string, RdfTerm>>();
        rows = directRows;
        if (projection.Computed.Count != 0 || projection.SelectAll || projection.Aggregates.Count != 0 ||
            body.Filters.Count != 0 ||
            body.Elements is not [PatternElement { Pattern.Path: null, Pattern: var pattern }])
        {
            return false;
        }

        string[] outputs = projection.OutputVariables.ToArray();
        bool requiresNestedBinding = outputs.Any(output =>
            PatternContainsNestedVariable(pattern, output));
        VisitSinglePattern(database, pattern, quad =>
        {
            var row = new Dictionary<string, RdfTerm>(outputs.Length, StringComparer.Ordinal);
            if (requiresNestedBinding)
            {
                if (!Bind(pattern.Subject, quad.Subject, row) ||
                    !Bind(pattern.Predicate!, quad.Predicate, row) ||
                    !Bind(pattern.Object, quad.Object, row) ||
                    pattern.Graph is not null && !Bind(pattern.Graph, quad.Graph!, row))
                {
                    return;
                }

                foreach (string name in row.Keys.Where(name => !outputs.Contains(name,
                             StringComparer.Ordinal)).ToArray())
                {
                    row.Remove(name);
                }
            }
            else
            {
                foreach (string output in outputs)
                {
                    if (VariableValue(pattern, output, quad) is { } value)
                    {
                        row[output] = value;
                    }
                }
            }

            AddBounded(directRows, row);
        });
        return true;
    }

    private static bool PatternContainsNestedVariable(Pattern pattern, string variable) =>
        ContainsNestedVariable(pattern.Subject, variable) ||
        ContainsNestedVariable(pattern.Predicate!, variable) ||
        ContainsNestedVariable(pattern.Object, variable) ||
        pattern.Graph is not null && ContainsNestedVariable(pattern.Graph, variable);

    private static bool ContainsNestedVariable(TermPattern pattern, string variable) =>
        pattern.Quoted is not null && (ContainsVariable(pattern.Quoted.Subject, variable) ||
            ContainsVariable(pattern.Quoted.Predicate, variable) ||
            ContainsVariable(pattern.Quoted.Object, variable));

    private static bool ContainsVariable(TermPattern pattern, string variable) =>
        pattern.Variable == variable || ContainsNestedVariable(pattern, variable);

    private static List<Dictionary<string, RdfTerm>> ApplyAggregation(
        IReadOnlyList<Dictionary<string, RdfTerm>> rows, SelectProjection projection,
        IReadOnlyList<string> groupVariables, RdfSparqlReadView database)
    {
        if (projection.Aggregates.Count == 0 && groupVariables.Count == 0)
        {
            return rows as List<Dictionary<string, RdfTerm>> ?? rows.ToList();
        }
        var groups = new Dictionary<SolutionKey, AggregateGroup>();
        foreach (Dictionary<string, RdfTerm> row in rows)
        {
            SolutionKey key = SolutionKey.Create(row, groupVariables);
            if (!groups.TryGetValue(key, out AggregateGroup? group))
            {
                if (groups.Count == MaximumResults)
                {
                    throw new RdfSparqlWorkLimitException(
                        $"SPARQL aggregation exceeded {MaximumResults} groups.");
                }

                groups.Add(key, group = new(row, groupVariables, projection.Aggregates, database));
            }
            group.Accumulate(row);
        }
        if (groups.Count == 0 && groupVariables.Count == 0)
        {
            groups.Add(SolutionKey.Create(new Dictionary<string, RdfTerm>()),
                new(new Dictionary<string, RdfTerm>(), groupVariables, projection.Aggregates, database));
        }

        var results = new List<Dictionary<string, RdfTerm>>(groups.Count);
        foreach (AggregateGroup group in groups.Values)
        {
            results.Add(group.FinalizeRow());
        }

        return results;
    }

    /// <summary>Executes the supported bounded ASK surface.</summary>
    internal static bool ExecuteAsk(IRdfDatabase databaseRoot, string query)
    {
        using RdfSparqlReadView database = CreateReadView(databaseRoot);
        return ExecuteAsk(database, query);
    }

    private static bool ExecuteAsk(RdfSparqlReadView database, string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        RequireSynchronousQuery(query);
        if (ParseDataset(query) is { } dataset)
        {
            using var selected = DatasetView(database, dataset);
            return ExecuteAsk(selected, dataset.Query);
        }
        return ((RdfSparqlAskResult)PrepareAsk(database, query).Run()).Value;
    }

    private static PreparedSparqlQuery PrepareAsk(RdfSparqlReadView database, string query)
    {
        (string queryBody, IReadOnlyDictionary<string, string> prefixes) = ParseProlog(query);
        var shape = ParseGraphQueryShape(queryBody, prefixes, false);

        Group body = ParseQueryBody(shape.Body, query, prefixes);
        var clock = UsesQueryClock(query) || prefixes.ContainsKey("$base")
            ? new QueryFunctionBindings(UsesQueryClock(query), prefixes.GetValueOrDefault("$base")) : null;
        body = clock?.Body(body) ?? body;
        Modifiers modifiers = ParseModifiers(shape.Tail, prefixes);
        modifiers = clock?.Options(modifiers) ?? modifiers;
        return new(body, modifiers, null, () => new RdfSparqlAskResult(database.Version, EvaluateSolutions(database, body, modifiers).Count > 0));
    }

    /// <summary>Executes a bounded CONSTRUCT query and returns distinct instantiated quads.</summary>
    internal static IReadOnlyList<RdfQuad> ExecuteConstruct(IRdfDatabase databaseRoot, string query)
    {
        using RdfSparqlReadView database = CreateReadView(databaseRoot);
        return ExecuteConstruct(database, query);
    }

    private static IReadOnlyList<RdfQuad> ExecuteConstruct(RdfSparqlReadView database, string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        RequireSynchronousQuery(query);
        if (ParseDataset(query) is { } dataset)
        {
            using var selected = DatasetView(database, dataset);
            return ExecuteConstruct(selected, dataset.Query);
        }
        return ((RdfSparqlGraphResult)PrepareConstruct(database, query).Run()).Quads;
    }

    private static PreparedSparqlQuery PrepareConstruct(RdfSparqlReadView database, string query)
    {
        (string queryBody, IReadOnlyDictionary<string, string> prefixes) = ParseProlog(query);
        var shape = ParseGraphQueryShape(queryBody, prefixes, true);

        var (templatePatterns, blankVariables) = ParseConstructTemplate(
            shape.Template ?? shape.Body, prefixes);

        Group body = ParseQueryBody(shape.Body, query, prefixes);
        var clock = UsesQueryClock(query) || prefixes.ContainsKey("$base")
            ? new QueryFunctionBindings(UsesQueryClock(query), prefixes.GetValueOrDefault("$base")) : null;
        body = clock?.Body(body) ?? body;
        Modifiers modifiers = ParseModifiers(shape.Tail, prefixes);
        modifiers = clock?.Options(modifiers) ?? modifiers;
        return new(body, modifiers, null, () => new RdfSparqlGraphResult(RdfSparqlQueryForm.Construct, database.Version,
            InstantiateTemplate(EvaluateSolutions(database, body, modifiers), templatePatterns, blankVariables)));
    }

    private static IReadOnlyList<RdfQuad> InstantiateTemplate(IEnumerable<IReadOnlyDictionary<string, RdfTerm>> rows,
        PatternElement[] templatePatterns, string[] blankVariables, RdfIri? defaultGraph = null)
    {
        var results = new HashSet<RdfQuad>();
        foreach (var row in rows)
        {
            var templateRow = new Dictionary<string, RdfTerm>(row, StringComparer.Ordinal);
            foreach (string variable in blankVariables)
            {
                templateRow.Add(variable, FreshBlankNode());
            }
            foreach (PatternElement element in templatePatterns)
            {
                RdfTerm? subject = Resolve(element.Pattern.Subject, templateRow);
                RdfTerm? predicate = Resolve(element.Pattern.Predicate!, templateRow);
                RdfTerm? obj = Resolve(element.Pattern.Object, templateRow);
                RdfTerm? graph = element.Pattern.Graph is null ? defaultGraph : Resolve(element.Pattern.Graph, templateRow);
                if (subject is null or RdfLiteral || predicate is not RdfIri iri || obj is null ||
                    graph is RdfLiteral or RdfTripleTerm || element.Pattern.Graph is not null && graph is null)
                {
                    continue;
                }

                results.Add(new RdfQuad(subject, iri, obj, graph).Validate());
                if (results.Count > MaximumResults)
                {
                    throw new RdfSparqlWorkLimitException($"SPARQL CONSTRUCT exceeded {MaximumResults} quads.");
                }
            }
        }

        return results.ToArray();
    }

    private static IEnumerable<PatternElement> ConstructPatterns(Group template, bool allowNamedGraphs)
    {
        if (template.Filters.Count > 0)
        {
            throw new InvalidDataException("A SPARQL template cannot contain filters.");
        }
        foreach (var element in template.Elements)
        {
            if (element is PatternElement { Pattern.Path: null } pattern)
            {
                yield return pattern;
            }
            else if (element is NamedGraphElement graph)
            {
                if (!allowNamedGraphs)
                {
                    throw new InvalidDataException("A CONSTRUCT query template accepts triples, not GRAPH clauses.");
                }
                foreach (var nested in ConstructPatterns(graph.Group, allowNamedGraphs))
                {
                    yield return nested with
                    {
                        Pattern = nested.Pattern with
                        {
                            Graph = graph.Graph
                        }
                    };
                }
            }
            else
            {
                throw new InvalidDataException("A SPARQL template requires triple patterns; GRAPH clauses are allowed only in update templates.");
            }
        }
    }

    /// <summary>Executes a bounded direct-subject DESCRIBE query.</summary>
    internal static IReadOnlyList<RdfQuad> ExecuteDescribe(IRdfDatabase databaseRoot, string query)
    {
        using RdfSparqlReadView database = CreateReadView(databaseRoot);
        return ExecuteDescribe(database, query);
    }

    private static IReadOnlyList<RdfQuad> ExecuteDescribe(RdfSparqlReadView database, string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        RequireSynchronousQuery(query);
        if (ParseDataset(query) is { } dataset)
        {
            using var selected = DatasetView(database, dataset);
            return ExecuteDescribe(selected, dataset.Query);
        }
        return ((RdfSparqlGraphResult)PrepareDescribe(database, query).Run()).Quads;
    }

    private static PreparedSparqlQuery PrepareDescribe(RdfSparqlReadView database, string query)
    {
        (string queryBody, IReadOnlyDictionary<string, string> prefixes) = ParseProlog(query);
        if (!queryBody.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Expected DESCRIBE.");
        }
        string[] targetTokens = Tokenize(queryBody[8..], prefixes);
        int targetIndex = 0;
        var targets = new List<TermPattern>();
        bool allTargets = targetTokens.Length > 0 && targetTokens[0] == "*";
        if (allTargets)
        {
            targetIndex++;
            if (targetIndex < targetTokens.Length && (targetTokens[targetIndex].StartsWith('?') || targetTokens[targetIndex].StartsWith('<')))
            {
                throw new InvalidDataException("DESCRIBE '*' cannot be combined with explicit targets.");
            }
        }
        while (!allTargets && targetIndex < targetTokens.Length &&
            (targetTokens[targetIndex].StartsWith('?') || targetTokens[targetIndex].StartsWith('<')))
        {
            targets.Add(ParsePatternTerm(targetTokens, ref targetIndex));
        }

        if (!allTargets && targets.Count == 0 || targets.Any(target => target.Quoted is not null || target.Constant is not null and not RdfIri))
        {
            throw new InvalidDataException("DESCRIBE requires variables, IRIs, or '*'.");
        }
        bool where = targetIndex < targetTokens.Length && targetTokens[targetIndex].Equals("WHERE", StringComparison.OrdinalIgnoreCase);
        if (where)
        {
            targetIndex++;
        }
        Group body;
        if (targetIndex < targetTokens.Length && targetTokens[targetIndex] == "{")
        {
            targetIndex++;
            body = ParseGroup(targetTokens, ref targetIndex, null, true, 0);
            ValidatePatternBlankScopes(body);
            if (CountPatterns(body) > MaximumPatterns)
            {
                throw new RdfSparqlWorkLimitException("DESCRIBE exceeds its pattern bound.");
            }
        }
        else if (where)
        {
            throw new InvalidDataException("WHERE requires a graph group.");
        }
        else
        {
            body = new([], []);
        }
        var clock = UsesQueryClock(query) || prefixes.ContainsKey("$base")
            ? new QueryFunctionBindings(UsesQueryClock(query), prefixes.GetValueOrDefault("$base")) : null;
        body = clock?.Body(body) ?? body;
        Modifiers modifiers = ParseModifiers(String.Join(" ", targetTokens[targetIndex..]), prefixes);
        modifiers = clock?.Options(modifiers) ?? modifiers;
        return new(body, modifiers, null, () => new RdfSparqlGraphResult(RdfSparqlQueryForm.Describe, database.Version,
            DescribeSolutions(database, body, modifiers, targets, allTargets)));
    }

    private static IReadOnlyList<RdfQuad> DescribeSolutions(RdfSparqlReadView database, Group body, Modifiers modifiers,
        IReadOnlyList<TermPattern> targets, bool allTargets)
    {
        List<Dictionary<string, RdfTerm>> rows = EvaluateSolutions(database, body, modifiers);
        var results = new HashSet<RdfQuad>();
        IEnumerable<RdfTerm?> subjects = targets.Select(target => target.Constant).Concat(rows.SelectMany(row =>
            allTargets ? row.Where(pair => !pair.Key.StartsWith('$')).Select(pair => pair.Value) :
                targets.Where(target => target.Variable is not null).Select(target => Resolve(target, row)))).Distinct();
        foreach (RdfTerm? subject in subjects)
        {
            if (subject is null or RdfLiteral or RdfTripleTerm)
            {
                continue;
            }
            foreach (RdfQuad quad in database.Find(new(Subject: subject)))
            {
                // The description is one RDF graph, regardless of which dataset
                // graphs supplied the subject's statements. Deduplicate triples.
                results.Add(quad.Graph is null ? quad : quad with
                {
                    Graph = null
                });
                if (results.Count > MaximumResults)
                {
                    throw new RdfSparqlWorkLimitException($"SPARQL DESCRIBE exceeded {MaximumResults} quads.");
                }
            }
        }

        return results.ToArray();
    }

    internal static RdfSparqlQueryResult ExecuteAtSnapshot(RdfDatabaseEngine database, string query,
        RdfSnapshotLease snapshot)
    {
        using var view = new RdfSparqlReadView(database, snapshot);
        return ExecuteView(view, query);
    }

    internal static RdfSparqlQueryResult ExecuteRelation(IReadOnlyList<RdfQuad> relation, string query,
        CancellationToken cancellation)
        => ExecuteDataset(new RelationRdfSparqlDatasetSnapshot(relation), query, cancellation);

    // Takes ownership of the snapshot, including when parsing or evaluation fails.
    // Profile adapters provide a consistent RDF view; they do not reimplement SPARQL.
    internal static RdfSparqlQueryResult ExecuteDataset(RdfSparqlDatasetSnapshot dataset, string query,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        using var view = new RdfSparqlReadView(dataset, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var result = ExecuteView(view, query);
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    private static RdfSparqlQueryResult ExecuteView(RdfSparqlReadView view, string query)
    {
        return QueryForm(query) switch
        {
            RdfSparqlQueryForm.Select => new RdfSparqlSelectResult(view.Version, Execute(view, query)),
            RdfSparqlQueryForm.Ask => new RdfSparqlAskResult(view.Version, ExecuteAsk(view, query)),
            RdfSparqlQueryForm.Construct => new RdfSparqlGraphResult(
                RdfSparqlQueryForm.Construct, view.Version, ExecuteConstruct(view, query)),
            RdfSparqlQueryForm.Describe => new RdfSparqlGraphResult(
                RdfSparqlQueryForm.Describe, view.Version, ExecuteDescribe(view, query)),
            _ => throw new InvalidDataException("The SPARQL query form is not supported.")
        };
    }

    private static RdfSparqlReadView CreateReadView(IRdfDatabase database)
    {
        RdfDatabaseEngine native = database as RdfDatabaseEngine ?? throw new ArgumentException(
            "This embedded SPARQL adapter requires the native Entity Forge RDF implementation.",
            nameof(database));
        return new(native, native.AcquireSnapshot());
    }

    /// <summary>Classifies a supported query after its optional prolog.</summary>
    public static RdfSparqlQueryForm QueryForm(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        (string body, _) = ParseProlog(query);
        string source = body.TrimStart();
        if (source.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            return RdfSparqlQueryForm.Select;
        }

        if (source.StartsWith("ASK", StringComparison.OrdinalIgnoreCase))
        {
            return RdfSparqlQueryForm.Ask;
        }

        if (source.StartsWith("CONSTRUCT", StringComparison.OrdinalIgnoreCase))
        {
            return RdfSparqlQueryForm.Construct;
        }

        if (source.StartsWith("DESCRIBE", StringComparison.OrdinalIgnoreCase))
        {
            return RdfSparqlQueryForm.Describe;
        }

        throw new NotSupportedException("The SPARQL query form is unsupported.");
    }

    private static List<Dictionary<string, RdfTerm>> EvaluateSolutions(RdfSparqlReadView database,
        Group body, Modifiers modifiers)
    {
        if (database.CompletedGraphRows is { } completed)
        {
            return completed;
        }
        // ASK and graph-result queries share SELECT's grouping and aggregate
        // expression rules, without introducing a projection of their own.
        SelectProjection projection;
        (projection, modifiers) = PrepareExpressions(new(false, [], [], []), modifiers);
        body = ApplyGroupingExpressions(body, modifiers);
        List<Dictionary<string, RdfTerm>> rows = database.RootRows ?? EvaluateGroup(database, body,
            [new Dictionary<string, RdfTerm>(StringComparer.Ordinal)]);
        rows = ApplyAggregation(rows, projection, modifiers.GroupVariables, database);
        if (modifiers.Having is { } having)
        {
            rows = rows.Where(row => Evaluate(database, having, row) == FilterResult.True).ToList();
        }
        if (modifiers.TrailingValues is { } values)
        {
            rows = EvaluateValues(values, rows, database.Cancellation);
        }
        if (modifiers.Order.Count != 0)
        {
            if (checked(modifiers.Offset + modifiers.Limit) < rows.Count / 2)
            {
                return SelectOrderedRows(rows, modifiers, database);
            }

            SortRows(rows, modifiers, database);
        }
        return rows.Skip(modifiers.Offset).Take(modifiers.Limit).ToList();
    }

    private static List<Dictionary<string, RdfTerm>> EvaluateGroup(RdfSparqlReadView database,
        Group group, List<Dictionary<string, RdfTerm>> rows,
        IReadOnlyDictionary<string, RdfTerm>? correlation = null)
    {
        // Scalar comparisons in a pure basic graph pattern are stable as soon
        // as their variables bind. Reject nonmatching rows before materializing
        // the remaining star, without changing OPTIONAL/UNION/BIND scope.
        var earlyFilters = group.Elements.All(element => element is PatternElement { Pattern.Path: null })
            ? group.Filters.SelectMany(ScalarConjuncts).ToArray() : [];
        var filteredVariables = earlyFilters.SelectMany(ComparisonVariables).ToHashSet(StringComparer.Ordinal);
        foreach (GroupElement element in OrderBasicGraphPatterns(database, group.Elements, rows, filteredVariables))
        {
            rows = element switch
            {
                PatternElement pattern => Match(database, pattern.Pattern, rows, earlyFilters),
                OptionalElement optional => EvaluateOptional(database, optional.Group, rows, correlation),
                ServiceElement service => database.Services is { } services && services.TryGetValue(service.Identity, out var values)
                    ? EvaluateValues(values, rows, database.Cancellation) : throw new NotSupportedException("SERVICE requires asynchronous query execution with configured HTTP endpoints."),
                NamedGraphElement named => EvaluateNamedGraph(database, named, rows, correlation),
                NestedGroupElement nested => EvaluateNestedGroup(database, nested.Group, rows, correlation),
                UnionElement union => EvaluateUnion(database, union.Groups, rows, correlation),
                MinusElement minus => EvaluateMinus(database, minus.Group, rows, correlation),
                SubqueryElement subquery => EvaluateSubquery(database, subquery.Query, rows, correlation),
                ValuesElement values => EvaluateValues(values, rows, database.Cancellation),
                BindElement bind => EvaluateBind(bind, rows, database),
                _ => throw new ArgumentOutOfRangeException(nameof(element))
            };
            if (rows.Count == 0)
            {
                break;
            }
        }
        if (group.Filters.Count != 0)
        {
            rows = rows.Where(row => group.Filters.All(filter =>
                Evaluate(database, filter, row) == FilterResult.True)).ToList();
        }

        return rows;
    }

    private static IEnumerable<ComparisonExpression> ScalarConjuncts(FilterExpression expression)
    {
        if (expression is ComparisonExpression { Left: TermValueExpression { Term.Quoted: null }, Right: TermValueExpression { Term.Quoted: null } } comparison)
        {
            yield return comparison;
        }
        else if (expression is BinaryBooleanExpression { Operator: "&&" } conjunction)
        {
            foreach (var comparisonPart in ScalarConjuncts(conjunction.Left).Concat(ScalarConjuncts(conjunction.Right)))
            {
                yield return comparisonPart;
            }
        }
    }

    private static IEnumerable<string> ComparisonVariables(ComparisonExpression comparison)
    {
        foreach (var value in new[] { comparison.Left, comparison.Right })
        {
            if (value is TermValueExpression { Term.Variable: { } variable })
            {
                yield return variable;
            }
        }
    }

    private static IEnumerable<GroupElement> OrderBasicGraphPatterns(RdfSparqlReadView database,
        IReadOnlyList<GroupElement> elements, IReadOnlyList<Dictionary<string, RdfTerm>> inputRows,
        IReadOnlySet<string>? filteredVariables = null)
    {
        var boundVariables = inputRows.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : inputRows.Select(row => row.Keys.ToHashSet(StringComparer.Ordinal))
                .Aggregate((left, right) =>
                {
                    left.IntersectWith(right);
                    return left;
                });
        for (int index = 0; index < elements.Count;)
        {
            if (elements[index] is not PatternElement)
            {
                yield return elements[index++];
                continue;
            }
            int end = index;
            while (end < elements.Count && elements[end] is PatternElement)
            {
                end++;
            }

            var remaining = elements.Skip(index).Take(end - index).Cast<PatternElement>()
                .Select((pattern, order) => (pattern, order)).ToList();
            while (remaining.Count > 0)
            {
                // Prefer a join on existing bindings over a disconnected scan,
                // even when the disconnected predicate has fewer total facts.
                var selected = remaining.MinBy(candidate => (
                    -PatternVariables(candidate.pattern.Pattern).Count(variable => filteredVariables?.Contains(variable) == true),
                    -PatternVariables(candidate.pattern.Pattern).Count(boundVariables.Contains),
                    EstimatePattern(database, candidate.pattern.Pattern),
                    -PatternBoundTermCount(candidate.pattern.Pattern),
                    candidate.order));
                yield return selected.pattern;
                boundVariables.UnionWith(PatternVariables(selected.pattern.Pattern));
                remaining.Remove(selected);
            }
            index = end;
        }
    }

    private static int EstimatePattern(RdfSparqlReadView database, Pattern pattern) => database.FragmentRows is { } rows ? rows[pattern].Count : pattern.Path is null
        ? pattern.Subject.Constant is RdfLiteral ? 0 : database.EstimateCandidates(new(pattern.Subject.Constant, pattern.Predicate?.Constant as RdfIri,
            pattern.Object.Constant, pattern.Graph?.Constant, MatchDefaultGraphOnly: pattern.Graph is null))
        : pattern.Subject.Constant is { } start
            ? EstimatePathSeed(database, pattern.Path, start, pattern.Graph?.Constant, false, pattern.Graph is null)
            : pattern.Object.Constant is { } finish
                ? EstimatePathSeed(database, pattern.Path, finish, pattern.Graph?.Constant, true, pattern.Graph is null)
                : Int32.MaxValue;

    // A path's final cardinality is unknown without traversal. Its indexed seed
    // is still a better planning heuristic than treating every anchored path as
    // infinitely expensive. This only orders joins; it never truncates results.
    private static int EstimatePathSeed(RdfSparqlReadView database, RdfPathExpression path,
        RdfTerm start, RdfTerm? graph, bool reverse, bool defaultOnly)
    {
        switch (path)
        {
            case RdfSequencePath sequence:
                return EstimatePathSeed(database, reverse ? sequence.Elements[^1] : sequence.Elements[0],
                    start, graph, reverse, defaultOnly);
            case RdfAlternativePath alternative:
                long total = 0;
                foreach (var branch in alternative.Alternatives)
                {
                    total += EstimatePathSeed(database, branch, start, graph, reverse, defaultOnly);
                }
                return (int)Math.Min(Int32.MaxValue, total);
            case RdfRepeatedPath repeated:
                return Math.Max(repeated.Minimum == 0 ? 1 : 0,
                    EstimatePathSeed(database, repeated.Operand, start, graph, reverse, defaultOnly));
            case RdfPredicatePath predicate:
                reverse ^= predicate.Inverse;
                if (!reverse && start is RdfLiteral)
                {
                    return 0;
                }
                return database.EstimateCandidates(reverse
                    ? new(Predicate: predicate.Predicate, Object: start, Graph: graph, MatchDefaultGraphOnly: defaultOnly)
                    : new(Subject: start, Predicate: predicate.Predicate, Graph: graph, MatchDefaultGraphOnly: defaultOnly));
            case RdfNegatedPath negated:
                reverse ^= negated.Inverse;
                if (!reverse && start is RdfLiteral)
                {
                    return 0;
                }
                return database.EstimateCandidates(reverse
                    ? new(Object: start, Graph: graph, MatchDefaultGraphOnly: defaultOnly)
                    : new(Subject: start, Graph: graph, MatchDefaultGraphOnly: defaultOnly));
            default:
                return Int32.MaxValue;
        }
    }

    private static IEnumerable<string> PatternVariables(Pattern pattern)
    {
        if (pattern.Subject.Variable is { } subject)
        {
            yield return subject;
        }

        if (pattern.Predicate?.Variable is { } predicate)
        {
            yield return predicate;
        }

        if (pattern.Object.Variable is { } obj)
        {
            yield return obj;
        }

        if (pattern.Graph?.Variable is { } graph)
        {
            yield return graph;
        }
    }

    private static int PatternBoundTermCount(Pattern pattern) =>
        (pattern.Subject.Constant is null ? 0 : 1) +
        (pattern.Predicate?.Constant is null ? 0 : 1) +
        (pattern.Object.Constant is null ? 0 : 1) +
        (pattern.Graph?.Constant is null ? 0 : 1);

    private static List<Dictionary<string, RdfTerm>> Match(RdfSparqlReadView database, Pattern pattern,
        IReadOnlyList<Dictionary<string, RdfTerm>> rows, IReadOnlyList<ComparisonExpression>? earlyFilters = null)
    {
        if (database.FragmentRows is not null)
        {
            return JoinFragmentRows(database, pattern, rows);
        }

        database.CheckCancellation();
        if (pattern.Path is not null)
        {
            return MatchPath(database, pattern, rows);
        }

        var next = new List<Dictionary<string, RdfTerm>>();
        foreach (Dictionary<string, RdfTerm> row in rows)
        {
            RdfTerm? subject = Resolve(pattern.Subject, row);
            RdfTerm? predicate = Resolve(pattern.Predicate!, row);
            RdfTerm? obj = Resolve(pattern.Object, row);
            RdfTerm? graph = pattern.Graph is null ? null : Resolve(pattern.Graph, row);
            if (subject is RdfLiteral || predicate is not null and not RdfIri || graph is RdfLiteral or RdfTripleTerm)
            {
                continue;
            }

            var introduced = new[] { pattern.Subject, pattern.Predicate, pattern.Object, pattern.Graph }.OfType<TermPattern>()
                .SelectMany(BindingVariables).Where(variable => !row.ContainsKey(variable)).Distinct(StringComparer.Ordinal).ToArray();
            var available = row.Keys.Concat(introduced).ToHashSet(StringComparer.Ordinal);
            var readyFilters = earlyFilters?.Where(filter => ComparisonVariables(filter).All(available.Contains)).ToArray();
            // Reuse one candidate binding while rejecting a predicate scan.
            // Only an admitted result owns a copied row; siblings never share it.
            var joined = new Dictionary<string, RdfTerm>(row, StringComparer.Ordinal);
            var candidates = database.FindInMemoryCandidates(subject, (RdfIri?)predicate,
                obj, graph, matchDefaultGraphOnly: pattern.Graph is null);
            foreach (RdfQuad quad in SelectIntegerCandidates(candidates, pattern, readyFilters, database.CheckCancellation))
            {
                database.CheckCancellation();
                if (subject is not null && !Equals(subject, quad.Subject) ||
                    predicate is not null && !Equals(predicate, quad.Predicate) ||
                    obj is not null && !Equals(obj, quad.Object) ||
                    graph is not null && !Equals(graph, quad.Graph) ||
                    pattern.Graph is null && quad.Graph is not null ||
                    pattern.Graph is not null && quad.Graph is null ||
                    !CanBind(pattern.Subject, quad.Subject, row) ||
                    !CanBind(pattern.Predicate!, quad.Predicate, row) ||
                    !CanBind(pattern.Object, quad.Object, row) ||
                    pattern.Graph is not null && !CanBind(pattern.Graph, quad.Graph!, row))
                {
                    continue;
                }

                foreach (var variable in introduced)
                {
                    joined.Remove(variable);
                }

                if (Bind(pattern.Subject, quad.Subject, joined) &&
                    Bind(pattern.Predicate!, quad.Predicate, joined) &&
                    Bind(pattern.Object, quad.Object, joined) &&
                    (pattern.Graph is null || Bind(pattern.Graph, quad.Graph!, joined)))
                {
                    if (readyFilters is null || readyFilters.All(filter => Evaluate(database, filter, joined) == FilterResult.True))
                    {
                        AddBounded(next, new(joined, StringComparer.Ordinal));
                    }
                }
            }
        }
        return next;
    }

    private static bool CanBind(TermPattern pattern, RdfTerm value,
        IReadOnlyDictionary<string, RdfTerm> row)
    {
        if (pattern.Constant is not null)
        {
            return Equals(pattern.Constant, value);
        }

        if (pattern.Variable is not null)
        {
            return !row.TryGetValue(pattern.Variable, out RdfTerm? existing) || Equals(existing, value);
        }

        return pattern.Quoted is not null && value is RdfTripleTerm triple &&
            CanBind(pattern.Quoted.Subject, triple.Subject, row) &&
            CanBind(pattern.Quoted.Predicate, triple.Predicate, row) &&
            CanBind(pattern.Quoted.Object, triple.Object, row);
    }

    private static IEnumerable<string> BindingVariables(TermPattern pattern)
    {
        if (pattern.Variable is { } variable)
        {
            yield return variable;
        }

        if (pattern.Quoted is { } quoted)
        {
            foreach (var nested in new[] { quoted.Subject, quoted.Predicate, quoted.Object }.SelectMany(BindingVariables))
            {
                yield return nested;
            }
        }
    }

    private static List<Dictionary<string, RdfTerm>> EvaluateOptional(RdfSparqlReadView database,
        Group optional, IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyDictionary<string, RdfTerm>? correlation)
    {
        var results = new List<Dictionary<string, RdfTerm>>();
        // OPTIONAL's own FILTER is a left-join condition. Nested group filters
        // remain scoped inside their group and cannot see unrelated outer bindings.
        var candidates = EvaluateGroup(database, optional with
        {
            Filters = []
        },
            [CorrelationSeed(correlation)], correlation);
        foreach (Dictionary<string, RdfTerm> row in rows)
        {
            List<Dictionary<string, RdfTerm>> matches = JoinSolutions([row], candidates, database.Cancellation)
                .Where(match => optional.Filters.All(filter => Evaluate(database, filter, match) == FilterResult.True)).ToList();
            if (matches.Count == 0)
            {
                AddBounded(results, row);
            }
            else
            {
                foreach (Dictionary<string, RdfTerm> match in matches)
                {
                    AddBounded(results, match);
                }
            }
        }
        return results;
    }

    private static List<Dictionary<string, RdfTerm>> EvaluateUnion(RdfSparqlReadView database,
        IReadOnlyList<Group> branches, IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyDictionary<string, RdfTerm>? correlation)
    {
        var results = new List<Dictionary<string, RdfTerm>>();
        foreach (Group branch in branches)
        {
            List<Dictionary<string, RdfTerm>> matches = EvaluateNestedGroup(database, branch, rows, correlation);
            foreach (Dictionary<string, RdfTerm> match in matches)
            {
                AddBounded(results, match);
            }
        }
        return results;
    }

    private static List<Dictionary<string, RdfTerm>> EvaluateMinus(RdfSparqlReadView database,
        Group minus, IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyDictionary<string, RdfTerm>? correlation)
    {
        List<Dictionary<string, RdfTerm>> candidates = EvaluateGroup(database, minus,
            [CorrelationSeed(correlation)], correlation);
        return MinusSolutions(rows, candidates, correlation, database.Cancellation);
    }

    internal static List<Dictionary<string, RdfTerm>> MinusSolutions(
        IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> candidates,
        IReadOnlyDictionary<string, RdfTerm>? correlation, CancellationToken cancellation)
    {
        var results = new List<Dictionary<string, RdfTerm>>();
        foreach (var row in QueryCancellation.Observe(rows, cancellation))
        {
            bool excluded = false;
            foreach (var candidate in QueryCancellation.Observe(candidates, cancellation))
            {
                if (CompatibleAndShared(row, candidate, correlation))
                {
                    excluded = true;
                    break;
                }
            }
            if (!excluded)
            {
                results.Add(row);
            }
        }
        return results;
    }

    private static bool CompatibleAndShared(IReadOnlyDictionary<string, RdfTerm> left,
        IReadOnlyDictionary<string, RdfTerm> right, IReadOnlyDictionary<string, RdfTerm>? correlation)
    {
        bool shared = false;
        foreach ((string variable, RdfTerm value) in left)
        {
            if (correlation?.ContainsKey(variable) == true || !right.TryGetValue(variable, out RdfTerm? other))
            {
                continue;
            }

            shared = true;
            if (!Equals(value, other))
            {
                return false;
            }
        }
        return shared;
    }

    private static List<Dictionary<string, RdfTerm>> EvaluateSubquery(RdfSparqlReadView database,
        ParsedSelectQuery query, IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyDictionary<string, RdfTerm>? correlation,
        List<Dictionary<string, RdfTerm>>? evaluated = null)
    {
        List<Dictionary<string, RdfTerm>> inner = evaluated ?? EvaluateGroup(database, query.Body,
            [CorrelationSeed(correlation)], correlation);
        inner = ApplyAggregation(inner, query.Projection, query.Modifiers.GroupVariables, database);
        if (query.Modifiers.Having is not null)
        {
            inner = inner.Where(row => Evaluate(database, query.Modifiers.Having, HavingRow(row, query.Projection)) == FilterResult.True).ToList();
        }

        bool orderingAppliedWindow = false;
        if (query.Modifiers.TrailingValues is { } values)
        {
            inner = EvaluateValues(values, inner, database.Cancellation);
        }
        inner = ApplyComputedProjection(inner, query.Projection, database);
        if (query.Modifiers.Order.Count != 0)
        {
            if (!query.Distinct && checked(query.Modifiers.Offset + query.Modifiers.Limit) < inner.Count / 2)
            {
                inner = SelectOrderedRows(inner, query.Modifiers, database);
                orderingAppliedWindow = true;
            }
            else
            {
                SortRows(inner, query.Modifiers, database);
            }
        }
        IEnumerable<IReadOnlyDictionary<string, RdfTerm>> projected = inner
            .Select(row => ProjectRow(row, query.Projection));
        if (query.Distinct)
        {
            projected = DistinctRows(projected);
        }

        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> selected = (orderingAppliedWindow
            ? projected : projected.Skip(query.Modifiers.Offset).Take(query.Modifiers.Limit)).ToArray();
        return JoinSolutions(rows, selected, database.Cancellation);
    }

    private static Dictionary<string, RdfTerm> CorrelationSeed(IReadOnlyDictionary<string, RdfTerm>? correlation) =>
        correlation is null ? new(StringComparer.Ordinal) : new(correlation, StringComparer.Ordinal);

    private static List<Dictionary<string, RdfTerm>> EvaluateNestedGroup(RdfSparqlReadView database,
        Group group, IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyDictionary<string, RdfTerm>? correlation)
    {
        // Ordinary groups are independent. Only EXISTS supplies a correlation environment.
        var selected = EvaluateGroup(database, group, [CorrelationSeed(correlation)], correlation);
        return JoinSolutions(rows, selected, database.Cancellation);
    }

    internal static List<Dictionary<string, RdfTerm>> JoinSolutions(
        IReadOnlyList<Dictionary<string, RdfTerm>> rows,
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> selected, CancellationToken cancellation)
    {
        var lookup = BuildJoinLookup(rows, selected, cancellation, out string? variable);
        var results = new List<Dictionary<string, RdfTerm>>();
        foreach (Dictionary<string, RdfTerm> outer in QueryCancellation.Observe(rows, cancellation))
        {
            IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> candidates = selected;
            if (lookup is not null && outer.TryGetValue(variable!, out RdfTerm? key))
            {
                candidates = lookup.TryGetValue(key, out var matches) ? matches : [];
            }
            foreach (IReadOnlyDictionary<string, RdfTerm> candidate in QueryCancellation.Observe(candidates, cancellation))
            {
                bool compatible = true;
                foreach (var item in outer)
                {
                    if (candidate.TryGetValue(item.Key, out RdfTerm? existing) && !Equals(existing, item.Value))
                    {
                        compatible = false;
                        break;
                    }
                }
                if (!compatible)
                {
                    continue;
                }

                var joined = new Dictionary<string, RdfTerm>(outer, StringComparer.Ordinal);
                foreach ((string name, RdfTerm value) in candidate)
                {
                    joined[name] = value;
                }

                AddBounded(results, joined);
            }
        }

        return results;
    }

    private static List<Dictionary<string, RdfTerm>> EvaluateValues(ValuesElement values,
        IReadOnlyList<Dictionary<string, RdfTerm>> rows, CancellationToken cancellation)
    {
        var results = new List<Dictionary<string, RdfTerm>>();
        foreach (Dictionary<string, RdfTerm> row in QueryCancellation.Observe(rows, cancellation))
        {
            foreach (IReadOnlyList<RdfTerm?> valueRow in QueryCancellation.Observe(values.Rows, cancellation))
            {
                bool compatible = true;
                for (int column = 0; column < values.Variables.Count; column++)
                {
                    RdfTerm? value = valueRow[column];
                    if (value is null)
                    {
                        continue;
                    }

                    string variable = values.Variables[column];
                    if (row.TryGetValue(variable, out RdfTerm? existing) && !Equals(existing, value))
                    {
                        compatible = false;
                        break;
                    }
                }
                if (!compatible)
                {
                    continue;
                }

                var joined = new Dictionary<string, RdfTerm>(row, StringComparer.Ordinal);
                for (int column = 0; column < values.Variables.Count; column++)
                {
                    if (valueRow[column] is { } value)
                    {
                        joined[values.Variables[column]] = value;
                    }
                }
                AddBounded(results, joined);
            }
        }

        return results;
    }

    private static List<Dictionary<string, RdfTerm>> EvaluateBind(BindElement bind,
        IReadOnlyList<Dictionary<string, RdfTerm>> rows, RdfSparqlReadView database)
    {
        var results = new List<Dictionary<string, RdfTerm>>(rows.Count);
        foreach (Dictionary<string, RdfTerm> row in rows)
        {
            RdfTerm? value = EvaluateValue(bind.Value, row, database);
            if (row.ContainsKey(bind.Variable))
            {
                throw new InvalidDataException("BIND cannot replace an in-scope variable.");
            }
            var bound = new Dictionary<string, RdfTerm>(row, StringComparer.Ordinal);
            PreserveBlankNodeScope(row, bound);
            if (value is not null)
            {
                bound[bind.Variable] = value;
            }
            AddBounded(results, bound);
        }
        return results;
    }

    private static void AddBounded(List<Dictionary<string, RdfTerm>> rows,
        Dictionary<string, RdfTerm> row)
    {
        rows.Add(row);
        if (rows.Count > MaximumResults)
        {
            throw new RdfSparqlWorkLimitException(
                $"SPARQL evaluation exceeded {MaximumResults} intermediate rows.");
        }
    }

    private static void ValidateGraphTerm(TermPattern pattern)
    {
        if (pattern.Quoted is not null || pattern.Variable is null && pattern.Constant is not RdfIri)
        {
            throw new InvalidDataException("A SPARQL 1.1 GRAPH clause requires an IRI or variable.");
        }
    }

    private static bool Bind(TermPattern pattern, RdfTerm value, Dictionary<string, RdfTerm> row)
    {
        if (pattern.Variable is not null)
        {
            if (row.TryGetValue(pattern.Variable, out RdfTerm? existing))
            {
                return Equals(existing, value);
            }

            row[pattern.Variable] = value;
            return true;
        }
        if (pattern.Constant is not null)
        {
            return Equals(pattern.Constant, value);
        }

        if (pattern.Quoted is null || value is not RdfTripleTerm triple)
        {
            return false;
        }

        return Bind(pattern.Quoted.Subject, triple.Subject, row) &&
            Bind(pattern.Quoted.Predicate, triple.Predicate, row) &&
            Bind(pattern.Quoted.Object, triple.Object, row);
    }

    private static RdfTerm? Resolve(TermPattern pattern, IReadOnlyDictionary<string, RdfTerm> row)
    {
        if (pattern.Variable is not null)
        {
            return row.GetValueOrDefault(pattern.Variable);
        }

        if (pattern.Constant is not null)
        {
            return pattern.Constant;
        }

        RdfTerm? subject = Resolve(pattern.Quoted!.Subject, row);
        RdfTerm? predicate = Resolve(pattern.Quoted.Predicate, row);
        RdfTerm? obj = Resolve(pattern.Quoted.Object, row);
        return subject is not null && predicate is RdfIri iri && obj is not null
            ? new RdfTripleTerm(subject, iri, obj)
            : null;
    }

    private sealed class RdfSparqlReadView(RdfSparqlDatasetSnapshot dataset,
        CancellationToken cancellation = default) : IDisposable
    {
        internal RdfSparqlReadView(RdfDatabaseEngine? database, RdfSnapshotLease? snapshot,
            IReadOnlyList<RdfQuad>? relation = null, CancellationToken cancellation = default)
            : this(database is null
                ? new RelationRdfSparqlDatasetSnapshot(relation ?? throw new ArgumentNullException(nameof(relation)))
                : new NativeRdfSparqlDatasetSnapshot(database, snapshot ?? throw new ArgumentNullException(nameof(snapshot))), cancellation)
        {
        }

        internal IReadOnlyDictionary<Guid, ValuesElement>? Services
        {
            get; set;
        }

        internal List<Dictionary<string, RdfTerm>>? CompletedGraphRows
        {
            get; set;
        }

        internal Dictionary<Pattern, List<Dictionary<string, RdfTerm>>>? FragmentRows
        {
            get; init;
        }

        internal List<Dictionary<string, RdfTerm>>? RootRows
        {
            get; set;
        }

        internal void CheckCancellation() => cancellation.ThrowIfCancellationRequested();

        internal CancellationToken Cancellation => cancellation;

        public DatabaseVersion Version => dataset.Version;

        internal RdfSparqlReadView WithActiveGraph(RdfTerm graph) =>
            new(new ActiveGraphRdfSparqlDatasetSnapshot(dataset, graph, cancellation), cancellation)
            {
                Services = Services
            };

        private T Read<T>(Func<T> read)
        {
            CheckCancellation();
            var result = read();
            CheckCancellation();
            return result;
        }

        public IReadOnlyList<RdfQuad> Find(RdfQuadPattern pattern) => Read(() => dataset.Find(pattern));

        public IReadOnlyList<RdfQuad> ReadPrefix(RdfQuadPattern pattern, int maximumResults, bool namedOnly = false) =>
            Read(() => dataset.ReadPrefix(pattern, maximumResults, namedOnly, cancellation));

        internal IReadOnlyList<RdfTerm> NamedGraphs(TermPattern graph)
        {
            if (graph.Constant is { } name)
            {
                return ReadPrefix(new(Graph: name), 1).Count == 0 ? [] : [name];
            }
            var names = ReadNamedGraphNames(100_001);
            if (names.Count > 100_000)
            {
                throw new RdfSparqlWorkLimitException("Named graph evaluation exceeds 100000 graph names.");
            }
            return names;
        }

        internal IReadOnlyList<RdfTerm> ReadNamedGraphNames(int maximumNames) =>
            Read(() => dataset.ReadNamedGraphNames(maximumNames, cancellation));

        public IReadOnlyList<RdfQuad> FindUnordered(RdfQuadPattern pattern) => Read(() => dataset.FindUnordered(pattern));

        public int EstimateCandidates(RdfQuadPattern pattern) => Read(() => dataset.EstimateCandidates(pattern));

        public IReadOnlySet<RdfQuad> FindInMemoryCandidates(RdfTerm? subject, RdfIri? predicate,
            RdfTerm? obj, RdfTerm? graph, bool matchDefaultGraphOnly) =>
            Read(() => subject is RdfLiteral ? (IReadOnlySet<RdfQuad>)new HashSet<RdfQuad>() :
                dataset.FindCandidates(new(subject, predicate, obj, graph, matchDefaultGraphOnly)));

        public IReadOnlyList<RdfQuad> FindInMemoryByPredicateOrderedSubject(RdfIri predicate, bool descending) =>
            Read(() => dataset.FindOrderedSubject(predicate, descending));

        public IReadOnlyList<RdfQuad> FindInMemoryByPatternOrderedSubject(RdfQuadPattern pattern, bool descending) =>
            Read(() => dataset.FindOrderedSubject(pattern, descending));

        public int CountInMemory(RdfIri predicate, RdfTerm obj, RdfTerm? graph) => Read(() => dataset.Count(predicate, obj, graph));

        public IReadOnlyDictionary<RdfTerm, int> CountDefaultGraphByObject(RdfIri predicate) =>
            Read(() => dataset.CountDefaultGraphByObject(predicate));

        public void Dispose() => dataset.Dispose();
    }

    [GeneratedRegex(@"\?" + VariableName)]
    private static partial Regex Variable();

    [GeneratedRegex(@"[ \t\r\n]*PREFIX[ \t\r\n]+(?<name>" + PrefixName + @"|):[ \t\r\n]*<(?<iri>[^>]*)>[ \t\r\n]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixDeclaration();

    [GeneratedRegex("(?:SERVICE|SILENT|DELETE|INSERT|DATA|USING|NAMED|SELECT|WHERE|DISTINCT|REDUCED|GROUP|BY|HAVING|ORDER|ASC|DESC|LIMIT|OFFSET|GRAPH|FILTER|NOT|EXISTS|OPTIONAL|MINUS|UNION|VALUES|BIND|AS|UNDEF|BOUND|isURI|isNumeric|STRENDS|ABS|CEIL|FLOOR|ROUND|ENCODE_FOR_URI|MD5|SHA1|SHA256|SHA384|SHA512|isIRI|isBlank|isLiteral|STRSTARTS|CONTAINS|REGEX|LANGMATCHES|LANG|DATATYPE|STRLEN|UCASE|LCASE|STR|IRI|URI|BNODE|NOW|UUID|STRUUID|RAND|COALESCE|IF|CONCAT|STRBEFORE|STRAFTER|YEAR|MONTH|DAY|HOURS|MINUTES|SECONDS|TIMEZONE|TZ|SUBSTR|REPLACE|STRDT|STRLANG|sameTerm|IN|GROUP_CONCAT|SEPARATOR|SAMPLE|COUNT|SUM|MIN|MAX|AVG)\\b(?!:)|" +
        "<<|>>|&&|\\|\\||<=|>=|!=|=|!|<[^<>\"{}|^`\\\\\\u0000-\\u0020]*>|<|>|,|\\[\\s*\\]|\\[|\\]|\\?" + VariableName + "|" + BlankNodeLabel + "|" +
        "[+-]?(?:(?:[0-9]+\\.[0-9]*|\\.[0-9]+|[0-9]+)[eE][+-]?[0-9]+|[0-9]*\\.[0-9]+|[0-9]+)|\"\"\"(?:\\\\.|\"{1,2}(?!\")|[^\"])*\"\"\"(?:@[A-Za-z0-9-]+|\\^\\^(?:<[^<>\"{}|^`\\\\\\u0000-\\u0020]*>|" + QualifiedName + "))?|" +
        "\"(?:\\\\.|[^\"])*\"(?:@[A-Za-z0-9-]+|\\^\\^(?:<[^<>\"{}|^`\\\\\\u0000-\\u0020]*>|" + QualifiedName + "))?|" +
        "" + QualifiedName + "|true\\b|false\\b|a\\b|[-/|^*+?]|[{}().;]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex("^(?:\"\"\"(?<longValue>(?:\\\\.|\"{1,2}(?!\")|[^\"])*)\"\"\"|\"(?<value>(?:\\\\.|[^\"])*)\")(?:@(?<lang>[A-Za-z0-9-]+)|\\^\\^<(?<type>[^>]+)>)?$")]
    private static partial Regex Literal();


}
