using System.Globalization;
using System.Text.RegularExpressions;
using EntityForge.Db.Graph;
using EntityForge.Db.Query.Planning;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

internal sealed record GqlRowStage(string Operation, IReadOnlyList<GqlPropertyProjectionTerm>? Terms = null,
    GraphScalarExpression? Expression = null, string? Alias = null, bool Distinct = false,
    GraphPatternAggregation? Aggregation = null, int Count = 0,
    IReadOnlyList<(GraphScalarExpression Expression, bool Descending, bool? NullsFirst)>? Order = null,
    CompiledGraphGqlQuery? MatchPlan = null, CompiledGraphGqlQuery? CallPlan = null,
    IReadOnlyList<string>? Imports = null, bool PreserveImports = false, GqlPathBinding? PathBinding = null,
    IReadOnlyList<GqlRowStage>? ValueSubqueries = null, string? InputValueAlias = null,
    IReadOnlyList<string>? DiscardBindings = null, IReadOnlyDictionary<string, string>? LocalImports = null,
    bool ProjectionModifier = false, IReadOnlyList<GqlMatchComponent>? MatchComponents = null,
    IReadOnlyList<string>? OrderingInputs = null, string? PositionAlias = null, bool OneBasedPosition = false,
    bool TableInput = false);

internal sealed record GqlMatchComponent(CompiledGraphGqlQuery Plan, GqlPathBinding? Path);

internal sealed partial record GqlRowPipeline(IReadOnlyList<GqlRowStage> Stages, bool SingletonSeed = false)
{
    internal QueryResultEnvelope Apply(QueryResultEnvelope envelope, CancellationToken cancellation = default,
        IReadOnlyDictionary<string, QueryValue>? imports = null, GqlExecutionBudget? executionBudget = null)
    {
        var source = (GraphPatternResult)envelope.Result;
        bool nullEliminated = false;
        var nodeLookup = new GqlNodeLookup(source.Graph);
        executionBudget ??= new GqlExecutionBudget();
        var rows = SingletonSeed ? new List<GraphPatternMatch> { new([], []) } : source.Matches.ToList();
        RequireBound(rows.Count);
        foreach (var stage in Stages)
        {
            cancellation.ThrowIfCancellationRequested();
            QueryValue Value(GraphPatternMatch row, GraphScalarExpression expression)
            {
                cancellation.ThrowIfCancellationRequested();
                return GraphScalarEvaluator.Evaluate(expression, (binding, property) => binding == "__row"
                    ? row.Bindings.GetValueOrDefault(property, UnboundValue.Instance)
                    : GraphGqlPropertyProjectionShaper.Evaluate(row, new(binding[9..], property, null)),
                    nodeLookup.Budget(executionBudget.SpendPathWork, cancellation));
            }
            switch (stage.Operation)
            {
                case "MATCH":
                case "OPTIONAL MATCH":
                case "CALL":
                case "OPTIONAL CALL":
                    throw new InvalidOperationException("Read stages require captured-snapshot execution.");
                case "PROJECT":
                    if (stage.Aggregation is { } aggregate)
                    {
                        string Key(GraphPatternMatch row) => RowKey(aggregate.GroupBy.Select(key => Value(row, key.Expression!)));
                        var groups = QueryCancellation.Observe(rows, cancellation).GroupBy(Key, StringComparer.Ordinal)
                            .Select(group => group.ToArray()).ToList();
                        if (groups.Count == 0 && aggregate.GroupBy.Count == 0)
                        {
                            groups.Add([]);
                        }
                        rows = groups.Select(group => new GraphPatternMatch(
                            aggregate.GroupBy.Select(key => new KeyValuePair<string, QueryValue>(key.Alias, Value(group[0], key.Expression!)))
                                .Concat(aggregate.Aggregates.Select(item => new KeyValuePair<string, QueryValue>(item.Alias,
                                    BoundedGraphPatternExecutor.AggregateValues(item, group.Select(row => item.Expression is { } expression
                                        ? Value(row, expression) : new LiteralValue("1", QueryScalarKind.Integer)),
                                        cancellation, () => nullEliminated = true)))), [])).ToList();
                    }
                    else
                    {
                        rows = rows.Select(row =>
                        {
                            var bindings = stage.Terms!.Select(term => new KeyValuePair<string, QueryValue>(
                                term.Alias, Value(row, term.Expression))).ToArray();
                            var edges = bindings.SelectMany(binding => ReturnedEdges(binding.Value)).ToHashSet();
                            return new GraphPatternMatch(bindings, stage.Distinct ? [] :
                                row.Paths.Where(path => path.Elements.OfType<EdgeValue>().Any(edges.Contains)));
                        }).ToList();
                    }
                    if (stage.Distinct)
                    {
                        rows = rows.DistinctBy(row => RowKey(row.Bindings.Values)).ToList();
                    }
                    if (stage.PreserveImports && imports is not null)
                    {
                        rows = rows.Select(row =>
                        {
                            var bindings = new Dictionary<string, QueryValue>(row.Bindings, StringComparer.Ordinal);
                            foreach (var item in imports)
                            {
                                bindings.TryAdd(item.Key, item.Value);
                            }
                            return new GraphPatternMatch(bindings, row.Paths);
                        }).ToList();
                    }
                    break;
                case "WHERE":
                case "FILTER":
                    rows = rows.Where(row => Includes(Value(row, stage.Expression!))).ToList();
                    if (stage.InputValueAlias is { } predicateValue)
                    {
                        rows = rows.Select(row => new GraphPatternMatch(row.Bindings.Where(pair => pair.Key != predicateValue), row.Paths)).ToList();
                    }
                    break;
                case "UNWIND":
                    var expanded = new List<GraphPatternMatch>();
                    foreach (var row in rows)
                    {
                        QueryValue value = Value(row, stage.Expression!);
                        IEnumerable<QueryValue> items = value is ListValue list ? list.Items : value is NullValue or UnboundValue ? []
                            : throw new ArgumentException("FOR requires a list value.");
                        int position = stage.OneBasedPosition ? 1 : 0;
                        foreach (var item in items)
                        {
                            RequireBound(expanded.Count + 1);
                            var bindings = new Dictionary<string, QueryValue>(row.Bindings, StringComparer.Ordinal) { [stage.Alias!] = item };
                            if (stage.PositionAlias is { } positionAlias)
                            {
                                bindings.Add(positionAlias, new LiteralValue(position.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer));
                            }
                            position++;
                            if (stage.InputValueAlias is { } consumed)
                            {
                                bindings.Remove(consumed);
                            }
                            expanded.Add(new(bindings, row.Paths));
                        }
                    }
                    rows = expanded;
                    break;
                case "ORDER BY":
                    {
                        // Evaluate sort keys once and use the input position to preserve ties.
                        var keyedRows = rows.Select((row, index) => (Row: row, Index: index,
                            Keys: stage.Order!.Select(order => Value(row, order.Expression)).ToArray())).ToArray();
                        QueryComparisonSort.Sort(keyedRows, (left, right) =>
                            {
                                for (int i = 0; i < stage.Order!.Count; i++)
                                {
                                    if (stage.Order[i].NullsFirst is { } nullsFirst)
                                    {
                                        bool leftNull = left.Keys[i] is NullValue or UnboundValue;
                                        bool rightNull = right.Keys[i] is NullValue or UnboundValue;
                                        if (leftNull != rightNull)
                                        {
                                            return leftNull == nullsFirst ? -1 : 1;
                                        }
                                    }
                                    int compare = GraphScalarEvaluator.CompareForOrder(left.Keys[i], right.Keys[i]);
                                    if (compare != 0)
                                    {
                                        return stage.Order[i].Descending ? -compare : compare;
                                    }
                                }
                                return left.Index.CompareTo(right.Index);
                            }, cancellation);
                        rows = keyedRows.Select(item => item.Row).ToList();
                        break;
                    }
                case "LIMIT":
                    rows = rows.Take(stage.Count).ToList();
                    break;
                case "SKIP":
                    rows = rows.Skip(stage.Count).ToList();
                    break;
            }
            if (stage.DiscardBindings is { Count: > 0 } discarded)
            {
                rows = rows.Select(row =>
                {
                    var bindings = row.Bindings.Where(pair => !discarded.Contains(pair.Key)).ToArray();
                    var edges = bindings.SelectMany(binding => ReturnedEdges(binding.Value)).ToHashSet();
                    return new GraphPatternMatch(bindings,
                        row.Paths.Where(path => path.Elements.OfType<EdgeValue>().Any(edges.Contains)));
                }).ToList();
            }
            RequireBound(rows.Count);
        }
        return new(envelope.QueryId, new GraphPatternResult(source.Graph, rows), envelope.CorrelationId,
            envelope.Evidence, envelope.SemanticModels, envelope.Diagnostics
                .Concat(nullEliminated ? [BoundedGraphPatternExecutor.NullEliminatedDiagnostic] : []).Distinct());
    }

    internal static string RowKey(IEnumerable<QueryValue> values) => String.Concat(values.Select(value =>
    {
        string key = GraphScalarEvaluator.DistinctKey(value);
        return key.Length.ToString(CultureInfo.InvariantCulture) + ":" + key;
    }));

    private static bool Includes(QueryValue value) => value switch
    {
        NullValue or UnboundValue => false,
        LiteralValue { ScalarKind: QueryScalarKind.Boolean } boolean => boolean.LexicalValue == "true",
        _ => throw new ArgumentException("WHERE requires a Boolean or null value.")
    };

    private static IEnumerable<EdgeValue> ReturnedEdges(QueryValue value) => value switch
    {
        EdgeValue edge => [edge],
        PathValue path => path.Elements.OfType<EdgeValue>(),
        ListValue list => list.Items.SelectMany(ReturnedEdges),
        MapValue map => map.Values.Values.SelectMany(ReturnedEdges),
        _ => []
    };

    private static void RequireBound(int count)
    {
        if (count > GraphGqlQueryCompiler.MaximumAggregateInputRows)
        {
            throw new ArgumentException("A GQL row stage exceeds 10,000 rows.");
        }
    }
}

internal sealed partial class GraphGqlQueryCompiler
{
    private static readonly Regex EntityFunctionPattern = new(
        @"\b(?:KEYS|PROPERTIES|PROPERTY_EXISTS|LABELS|TYPE|ELEMENTID|ELEMENT_ID|STARTNODE|ENDNODE|ANY|ALL|NONE|SINGLE|REDUCE|ALLREDUCE)\s*\(|\b" + GraphGqlIdentifiers.Pattern + @"\s*:(?!:)|\bIS\s+(?:NOT\s+)?(?:LABELED|TYPED|DIRECTED|SOURCE|DESTINATION)\b|::|\bPATH\s*\[|\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{|\[\s*" + GraphGqlIdentifiers.Pattern + @"\s+IN\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal CompiledGraphGqlQuery CompileImportedRows(string source, IReadOnlySet<string> scope,
        IReadOnlySet<string>? retainedImports = null, int depth = 0)
    {
        if (source == "RETURN *" && scope.Count is > 0 and <= 32 && retainedImports is null &&
            scope.All(GraphGqlIdentifiers.Whole.IsMatch))
        {
            // Generated mutation-selection projection: no expressions, clocks,
            // parameters or retained values to compile or cache.
            var terms = scope.Select(name => new GqlPropertyProjectionTerm(new("__row", name, null), name)).ToArray();
            return new(new(new("__seed"), maximumMatches: 0), null, Pipeline: new([new("PROJECT", terms)], true));
        }
        source = LowerAggregateExpressions(LowerFor(LowerPatternComprehensions(NormalizeQuotedStrings(source))));
        return TryCompileRowPipeline(source, null, scope, depth, preserveImports: retainedImports is not null, retainedImports: retainedImports)
            ?? throw Invalid("A mutation read stage requires a row pipeline.");
    }

    private bool RequiresMatchProduct(string body, long? snapshot)
    {
        bool repeatable = ReadMatchMode(ref body);
        string[] components = SplitPatternClauses(body).ToArray();
        if (components.Length < 2)
        {
            return false;
        }
        var bound = new HashSet<string>(StringComparer.Ordinal);
        foreach (string component in components)
        {
            if (PathAssignment().IsMatch(component) || CountedPathSelector().IsMatch(component) || ExplicitPathMode().IsMatch(component))
            {
                return true;
            }
            var probe = Compile(new("MATCH " + (repeatable ? "REPEATABLE ELEMENTS " : "") + component + " RETURN COUNT(*) AS __probe", snapshot), patternOnly: true);
            if (probe.Query.Start.Binding.StartsWith('$') || bound.Count > 0 && !bound.Contains(probe.Query.Start.Binding) || probe.Query.Relationships.Count == 0)
            {
                return true;
            }
            bound.Add(probe.Query.Start.Binding);
            bound.UnionWith(probe.Query.Relationships.Select(edge => edge.Target.Binding));
        }
        return false;
    }

    private static bool ReadMatchMode(ref string body)
    {
        var mode = GqlRegex.Match(body, @"^\s*(?<repeat>REPEATABLE\s+ELEMENTS|DIFFERENT\s+RELATIONSHIPS)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!mode.Success)
        {
            return false;
        }
        body = body[mode.Length..];
        return mode.Groups["repeat"].Value.StartsWith("REPEATABLE", StringComparison.OrdinalIgnoreCase);
    }

    private static string LowerFor(string source)
    {
        var clauses = WithClauses(source);
        for (int i = clauses.Count - 1; i >= 0; i--)
        {
            var clause = clauses[i];
            if (clause.Word != "FOR")
            {
                continue;
            }
            int end = i + 1 < clauses.Count ? clauses[i + 1].Start : source.Length;
            string body = source[(clause.Start + clause.Length)..end];
            var header = GqlRegex.Match(body, @"^\s*(?<alias>" + GraphGqlIdentifiers.Pattern + @")\s+IN\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!header.Success)
            {
                throw Invalid("FOR requires an alias followed by IN and a list expression.");
            }
            string expression = body[header.Length..].Trim();
            var position = GqlRegex.Match(MaskQuotedStrings(expression), @"\s+(?<mode>__ORDINALITY|__OFFSET)\s+(?<name>" + GraphGqlIdentifiers.Pattern + @")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            string suffix = position.Success ? " " + expression[position.Index..].Trim() : "";
            if (position.Success)
            {
                expression = expression[..position.Index].TrimEnd();
            }
            source = source[..clause.Start] + "UNWIND " + expression +
                " AS " + header.Groups["alias"].Value + suffix + " " + source[end..];
        }
        return source;
    }

    private CompiledGraphGqlQuery? TryCompileRowPipeline(string source, long? snapshot,
        IReadOnlySet<string>? imports = null, int callDepth = 0, bool preserveImports = true,
        IReadOnlySet<string>? retainedImports = null)
    {
        retainedImports ??= imports;
        var clauses = WithClauses(source);
        for (int clauseIndex = 0; clauseIndex < clauses.Count; clauseIndex++)
        {
            var clause = clauses[clauseIndex];
            if (clause.Word != "MATCH")
            {
                continue;
            }
            int end = clauseIndex + 1 < clauses.Count ? clauses[clauseIndex + 1].Start : source.Length;
            string[] components = SplitPatternClauses(source[(clause.Start + clause.Length)..end]).ToArray();
            if (components.Length < 2)
            {
                continue;
            }
            if (components.Length > MaximumPatternClauses)
            {
                throw Invalid($"A MATCH cannot exceed {MaximumPatternClauses} pattern components.");
            }
        }
        bool startsWithProduct = clauses.Count > 0 && clauses[0].Word == "MATCH" &&
            RequiresMatchProduct(source[(clauses[0].Start + clauses[0].Length)..(clauses.Count > 1 ? clauses[1].Start : source.Length)], snapshot);
        string initialPattern = clauses.Count > 0 ? source[(clauses[0].Start + clauses[0].Length)..] : String.Empty;
        _ = ReadMatchMode(ref initialPattern);
        bool startsWithPath = clauses.Count > 0 && clauses[0].Word == "MATCH" &&
            (PathAssignment().IsMatch(initialPattern) || CountedPathSelector().IsMatch(initialPattern) || ExplicitPathMode().IsMatch(initialPattern));
        bool startsWithGroup = clauses.Count > 0 && clauses[0].Word == "MATCH" &&
            GqlRegex.IsMatch(MaskQuotedStrings(initialPattern), @"\[[^\]]*\*|\]\s*(?:->|-)?\s*(?:\{|\+|\*)", RegexOptions.CultureInvariant);
        bool singleton = imports is not null || startsWithPath || startsWithProduct || startsWithGroup || clauses.Count > 0 && clauses[0].Start == 0 && clauses[0].Word is "RETURN" or "WITH" or "UNWIND" or "FILTER" or "LET" or "OPTIONAL MATCH" or "CALL" or "OPTIONAL CALL";
        int first = singleton ? 0 : clauses.FindIndex(clause => clause.Word is "WITH" or "UNWIND" or "FILTER" or "LET" or "CALL" or "OPTIONAL CALL");
        int finalProjection = clauses.FindIndex(clause => clause.Word == "RETURN");
        int standaloneModifier = clauses.FindIndex(clause => clause.Word is "ORDER BY" or "SKIP" or "OFFSET" or "LIMIT");
        if (standaloneModifier > 0 && standaloneModifier < finalProjection && (first < 0 || standaloneModifier < first))
        {
            first = standaloneModifier;
        }
        if (!singleton && finalProjection >= 0 && !IsWholeGraphProjection(source[(clauses[finalProjection].Start + clauses[finalProjection].Length)..]))
        {
            int optional = clauses.FindIndex(clause => clause.Word == "OPTIONAL MATCH");
            if (optional >= 0 && (first < 0 || optional < first))
            {
                first = optional;
            }
        }
        int initialWhere = clauses.FindIndex(clause => clause.Word == "WHERE");
        int nextMatch = clauses.Count > 1 ? clauses.FindIndex(1, clause => clause.Word == "MATCH") : -1;
        if (nextMatch > 0 && (first < 0 || nextMatch < first))
        {
            int boundary = clauses[nextMatch].Start;
            return Compile(new(source[..boundary] + "WITH * " + source[boundary..], snapshot));
        }
        bool EntityFunctions(string text) => EntityFunctionPattern.IsMatch(MaskQuotedStrings(text));
        if (initialWhere >= 0 && (first < 0 || initialWhere < first) && EntityFunctions(source[clauses[initialWhere].Start..(initialWhere + 1 < clauses.Count ? clauses[initialWhere + 1].Start : source.Length)]))
        {
            int boundary = clauses[initialWhere].Start;
            return Compile(new(source[..boundary] + "WITH * " + source[boundary..], snapshot));
        }
        if (first < 0)
        {
            if (finalProjection >= 0 && (GqlRegex.IsMatch(source[clauses[finalProjection].Start..], "\\s+AS\\s+\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) || EntityFunctions(source[clauses[finalProjection].Start..]) ||
                GqlRegex.IsMatch(MaskQuotedStrings(source[clauses[finalProjection].Start..]),
                    @"\b(?:NULLS\s+(?:FIRST|LAST)|ASCENDING|DESCENDING)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                GqlRegex.IsMatch(MaskQuotedStrings(source[clauses[finalProjection].Start..]),
                    @"(?:^RETURN\s+(?:(?:DISTINCT|ALL)\s+)?|,)\s*" + GraphGqlIdentifiers.Pattern + @"\s+AS\s+" + GraphGqlIdentifiers.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                clauses.Skip(finalProjection + 1).Any(clause => clause.Word == "ORDER BY") &&
                    !Return.IsMatch(source[clauses[finalProjection].Start..]) &&
                    !PropertyReturn.IsMatch(source[clauses[finalProjection].Start..]) &&
                    ParseAggregateReturn(source[clauses[finalProjection].Start..]) is null ||
                clauses.Skip(finalProjection + 1).Any(clause => clause.Word == "GROUP BY") &&
                    ParseAggregateReturn(source[clauses[finalProjection].Start..]) is null ||
                MaskQuotedStrings(source[clauses[finalProjection].Start..]).Contains('{')))
            {
                int boundary = clauses[finalProjection].Start;
                return Compile(new(source[..boundary] + "WITH * " + source[boundary..], snapshot));
            }
            return null;
        }
        int returned = clauses.FindIndex(first, clause => clause.Word == "RETURN");
        if (returned < 0 || clauses.Count - first > 64 || clauses.Count(clause => clause.Word == "WITH") > 16)
        {
            throw Invalid("A staged query requires RETURN and at most 64 row operators.");
        }
        string prefix = source[..clauses[first].Start];
        CompiledGraphGqlQuery seed;
        if (singleton)
        {
            // Retain ordinary snapshot/authorization handling without depending
            // on the presence or number of graph entities for a row-only query.
            // This fixed internal seed has no user syntax or expressions. Build
            // its empty plan directly instead of recursively compiling the same
            // generated query for every imported row stage.
            seed = new(new(new("__seed"), maximumMatches: 0), snapshot);
        }
        else
        {
            var probe = Compile(new(prefix + " RETURN COUNT(*) AS seed", snapshot));
            bool named = new[] { probe.Query.Start.Binding }.Concat(probe.Query.Relationships.SelectMany(
                relationship => new[] { relationship.Binding, relationship.Target.Binding })).Any(name => !name.StartsWith('$'));
            seed = Compile(new(prefix + (named ? " RETURN *" : " RETURN 1 AS __seed") + " LIMIT 10001", snapshot));
        }
        var scope = (imports ?? (singleton ? [] : seed.Query.ReturnedBindings.Where(name => !name.StartsWith('$'))).ToHashSet(StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var stages = new List<GqlRowStage>();
        var modifiers = new HashSet<string>(StringComparer.Ordinal);
        bool modifierScope = false;
        bool emptyBindingTable = false;
        bool final = false;
        bool explicitProductGraph = false;
        IReadOnlyList<string>? orderingInputs = null;
        for (int i = first; i < clauses.Count; i++)
        {
            var clause = clauses[i];
            string body = source[(clause.Start + clause.Length)..(i + 1 < clauses.Count ? clauses[i + 1].Start : source.Length)].Trim().TrimEnd(';').Trim();
            if (clause.Word is "MATCH" or "OPTIONAL MATCH" && !final)
            {
                modifierScope = false;
                bool repeatable = ReadMatchMode(ref body);
                var components = new List<GqlMatchComponent>();
                string[] patternComponents = SplitPatternClauses(body).ToArray();
                foreach (string component in patternComponents)
                {
                    string patternBody = component;
                    var assignment = PathAssignment().Match(patternBody);
                    string? pathAlias = assignment.Success ? assignment.Groups["name"].Value : null;
                    if (assignment.Success)
                    {
                        patternBody = patternBody[assignment.Length..];
                    }
                    var selector = CountedPathSelector().Match(patternBody);
                    if (selector.Success)
                    {
                        patternBody = patternBody[selector.Length..];
                        pathAlias ??= "$selectedPath" + stages.Count + "_" + components.Count;
                    }
                    var pathMode = ExplicitPathMode().Match(patternBody);
                    if (pathMode.Success)
                    {
                        patternBody = patternBody[pathMode.Length..];
                        pathAlias ??= "$modePath" + stages.Count + "_" + components.Count;
                    }
                    patternBody = (repeatable ? "REPEATABLE ELEMENTS " : "") + patternBody;
                    string selectedMode = pathMode.Success ? pathMode.Groups["mode"].Value : selector.Groups["pathMode"].Value;
                    GraphPatternPathMode traversalMode = Enum.TryParse<GraphPatternPathMode>(selectedMode, true, out var parsedMode)
                        ? parsedMode : GraphPatternPathMode.Walk;
                    int nativeCount = 1;
                    bool countedShortest = selector.Groups["mode"].Value.Equals("SHORTEST", StringComparison.OrdinalIgnoreCase) &&
                        (!selector.Groups["count"].Success ||
                         Int32.TryParse(selector.Groups["count"].Value, out nativeCount) && nativeCount is >= 0 and <= MaximumAggregateInputRows);
                    var nativeSelection = selector.Groups["all"].Success || countedShortest && selector.Groups["groups"].Success
                        ? GraphPatternPathSelection.AllShortest : selector.Groups["one"].Success || countedShortest
                        ? GraphPatternPathSelection.AnyShortest : GraphPatternPathSelection.All;
                    var probe = Compile(new("MATCH " + patternBody + " RETURN COUNT(*) AS seed", snapshot), patternOnly: true,
                        pathMode: traversalMode, singleWalkSelection: nativeSelection, singleWalkSelectionCount: nativeCount);
                    bool named = new[] { probe.Query.Start.Binding }.Concat(probe.Query.Relationships.SelectMany(
                        relationship => new[] { relationship.Binding, relationship.Target.Binding })).Any(name => !name.StartsWith('$'));
                    // Products need anonymous relationship identities too, to
                    // enforce one physical trail across all component patterns.
                    var matched = pathAlias is null && patternComponents.Length == 1
                        ? Compile(new("MATCH " + patternBody + (named ? " RETURN *" : " RETURN 1 AS __matchSeed") + " LIMIT 10001", snapshot), patternOnly: true) : PathReadPlan(probe);
                    var path = pathAlias is null ? null : CreatePathBinding(pathAlias, matched, scope);
                    if (pathMode.Success || selector.Groups["pathMode"].Success)
                    {
                        path = path! with
                        {
                            Mode = (pathMode.Success ? pathMode.Groups["mode"].Value : selector.Groups["pathMode"].Value).ToUpperInvariant()
                        };
                    }
                    if (selector.Success && !selector.Groups["every"].Success)
                    {
                        bool shortest = selector.Groups["all"].Success || selector.Groups["one"].Success;
                        string mode = shortest ? "SHORTEST" : selector.Groups["mode"].Value.ToUpperInvariant();
                        bool groups = selector.Groups["all"].Success || selector.Groups["groups"].Success;
                        if (!Int32.TryParse(shortest || !selector.Groups["count"].Success ? "1" : selector.Groups["count"].Value, out int count) || count is < 0 or > MaximumAggregateInputRows ||
                            matched.Query.Relationships.Any(edge => scope.Contains(edge.Binding)) ||
                            mode == "ANY" && groups)
                        {
                            throw Invalid("A counted selector requires 0-10000 paths, a connected chain and unbound relationship variables; GROUPS requires SHORTEST.");
                        }
                        path = path! with
                        {
                            Selector = mode,
                            Count = count,
                            Groups = groups
                        };
                    }
                    if (matched.Pipeline is not null || matched.Union is not null)
                    {
                        throw Invalid("A MATCH stage requires a graph pattern.");
                    }
                    scope.UnionWith(matched.Query.ReturnedBindings.Where(name => !name.StartsWith('$')));
                    if (pathAlias is not null && !pathAlias.StartsWith('$'))
                    {
                        scope.Add(pathAlias);
                    }
                    if (scope.Count > 32)
                    {
                        throw Invalid("A row stage cannot exceed 32 aliases.");
                    }
                    components.Add(new(matched, path));
                }
                if (components.Count is 0 or > MaximumPatternClauses)
                {
                    throw Invalid($"A MATCH requires 1-{MaximumPatternClauses} pattern components.");
                }
                stages.Add(new(clause.Word, MatchPlan: components[0].Plan, PathBinding: components[0].Path,
                    MatchComponents: components.Count > 1 ? components : null));
            }
            else if (clause.Word is "CALL" or "OPTIONAL CALL" && !final)
            {
                bool tableInput = body.StartsWith("TABLE {", StringComparison.Ordinal);
                var called = CompileCall(tableInput ? body[6..] : body, scope, snapshot, callDepth,
                    retainedImports: tableInput ? retainedImports ?? new HashSet<string>(StringComparer.Ordinal) : null);
                if (tableInput)
                {
                    // NEXT replaces the working table; only lexical imports survive it.
                    scope.Clear();
                    if (retainedImports is not null)
                    {
                        scope.UnionWith(retainedImports);
                    }
                }
                scope.UnionWith(CallColumns(called.Plan));
                // The internal table bridge temporarily names up to 32 output columns
                // alongside up to 32 lexical imports. Its generated WITH restores the
                // public column names and enforces the ordinary 32-column limit.
                if (scope.Count > (tableInput ? 64 : 32))
                {
                    throw Invalid("A CALL result cannot exceed 32 visible aliases.");
                }
                stages.Add(new(clause.Word, CallPlan: called.Plan, Imports: called.Imports, TableInput: tableInput));
                modifierScope = false;
            }
            else if (clause.Word is "WITH" or "RETURN")
            {
                if (final)
                {
                    throw Invalid("RETURN must be the final projection.");
                }
                final = clause.Word == "RETURN";
                if (final && startsWithProduct && body.Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
                {
                    body = "*";
                    explicitProductGraph = true;
                }
                modifiers.Clear();
                modifierScope = true;
                bool distinct = GqlRegex.IsMatch(body, @"^DISTINCT\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (distinct)
                {
                    body = body[8..].Trim();
                }
                else if (GqlRegex.IsMatch(body, @"^ALL\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    body = body[3..].Trim();
                }
                string grouping = String.Empty;
                if (i + 1 < clauses.Count && clauses[i + 1].Word == "GROUP BY")
                {
                    var group = clauses[++i];
                    grouping = " GROUP BY " + source[(group.Start + group.Length)..(i + 1 < clauses.Count ? clauses[i + 1].Start : source.Length)].Trim().TrimEnd(';').Trim();
                }
                var terms = new List<(string Name, string Expression)>();
                bool emptyProjection = !final && body == "()";
                foreach (string item in emptyProjection ? Array.Empty<string>() : SplitProjectionItems(body))
                {
                    if (item.Trim() == "*")
                    {
                        terms.AddRange(scope.Select(name => (name, name)));
                        continue;
                    }
                    var alias = GraphGqlIdentifiers.ProjectionAlias(item);
                    string expression = alias.Success ? item[..alias.Index].Trim() : item.Trim();
                    string name = alias.Success ? GraphGqlIdentifiers.DecodeAlias(alias) : expression;
                    // Lowered wildcard composition may select an imported column
                    // whose name cannot be written as a regular variable. Only
                    // an exact lookup of that same column is an identity projection.
                    if (preserveImports && retainedImports?.Contains(name) == true && expression != name &&
                        expression != "__YIELD_COLUMN(" + System.Text.Json.JsonSerializer.Serialize(name) + ")")
                    {
                        throw Invalid("An imported CALL variable cannot be rebound.");
                    }
                    if (!alias.Success && !GraphGqlIdentifiers.Whole.IsMatch(name))
                    {
                        throw Invalid("A computed row-stage column requires AS and an alias.");
                    }
                    terms.Add((name, expression));
                }
                bool permitsEmpty = emptyProjection || body == "*" && (!final || emptyBindingTable);
                if (terms.Count == 0 && !permitsEmpty || terms.Count > 32 ||
                    terms.Select(term => term.Name).Distinct(StringComparer.Ordinal).Count() != terms.Count)
                {
                    throw Invalid("A row projection requires one to 32 unique aliases.");
                }
                var projectionScope = scope.ToHashSet(StringComparer.Ordinal);
                var reserved = GqlRegex.Matches(MaskQuotedStrings(body), GraphGqlIdentifiers.Pattern).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
                for (int termIndex = 0; termIndex < terms.Count; termIndex++)
                {
                    var term = terms[termIndex];
                    if (!GqlRegex.IsMatch(MaskQuotedStrings(term.Expression), @"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        continue;
                    }
                    var aggregateCall = AggregateStart.Match(MaskReadSubqueryAggregates(MaskQuotedStrings(term.Expression)));
                    if (aggregateCall.Success && aggregateCall.Index == 0 && term.Expression.EndsWith(')'))
                    {
                        var arguments = SplitProjectionItems(term.Expression[aggregateCall.Length..^1]).ToArray();
                        for (int argumentIndex = 0; argumentIndex < arguments.Length; argumentIndex++)
                        {
                            string argument = arguments[argumentIndex].Trim();
                            if (!GqlRegex.IsMatch(MaskQuotedStrings(argument), @"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                            {
                                continue;
                            }
                            var quantifier = GqlRegex.Match(argument, @"^(?:DISTINCT|ALL)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                            var argumentStage = CompileValueExpression(argument[quantifier.Length..], scope, snapshot, callDepth, reserved);
                            stages.Add(argumentStage);
                            projectionScope.Add(argumentStage.Alias!);
                            reserved.Add(argumentStage.Alias!);
                            arguments[argumentIndex] = quantifier.Value + argumentStage.Alias!;
                        }
                        terms[termIndex] = (term.Name, term.Expression[..aggregateCall.Length] + String.Join(",", arguments) + ")");
                        continue;
                    }
                    var value = CompileValueExpression(term.Expression, scope, snapshot, callDepth, reserved);
                    stages.Add(value);
                    projectionScope.Add(value.Alias!);
                    reserved.Add(value.Alias!);
                    terms[termIndex] = (term.Name, value.Alias!);
                }
                var aggregate = ParseAggregateReturn("RETURN " + String.Join(",", terms.Select(term => term.Expression + " AS " + GraphGqlIdentifiers.QuoteAlias(term.Name))) + grouping, projectionScope);
                if (grouping.Length != 0 && aggregate is null)
                {
                    string keysText = grouping[" GROUP BY ".Length..].Trim();
                    var aliases = terms.GroupBy(term => term.Expression, StringComparer.Ordinal)
                        .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
                    var projectedKeys = terms.Select(term => term.Name).ToHashSet(StringComparer.Ordinal);
                    string[] keys = SplitProjectionItems(keysText).Select(key => key.Trim())
                        .Select(key => projectedKeys.Contains(key) ? key : aliases.GetValueOrDefault(key, key)).ToArray();
                    if (keysText == "()" || keys.Length != terms.Count ||
                        !keys.ToHashSet(StringComparer.Ordinal).SetEquals(terms.Select(term => term.Name)))
                    {
                        throw Invalid("GROUP BY without an aggregate currently requires exactly the projected keys and a nonempty grouping set.");
                    }
                    distinct = true;
                }
                stages.Add(new("PROJECT", aggregate is null ? terms.Select(term => new GqlPropertyProjectionTerm(body == "*" ? new GraphScalarExpression("__row", term.Name, null) : ParseExpression(term.Expression, projectionScope), term.Name)).ToArray() : null,
                    Distinct: distinct, Aggregation: aggregate, PreserveImports: preserveImports && !final && retainedImports is not null));
                var inputScope = scope;
                scope = terms.Select(term => term.Name).ToHashSet(StringComparer.Ordinal);
                emptyBindingTable = terms.Count == 0 && (emptyProjection || emptyBindingTable);
                if (preserveImports && !final && retainedImports is not null)
                {
                    scope.UnionWith(retainedImports);
                }
                if (!distinct && aggregate is null && i + 1 < clauses.Count && clauses[i + 1].Word == "ORDER BY")
                {
                    // Sorting a non-aggregated projection may inspect its input
                    // variables. Drop those extra bindings immediately after ORDER.
                    orderingInputs = inputScope.Except(scope).ToArray();
                    var project = stages[^1];
                    stages[^1] = project with
                    {
                        OrderingInputs = orderingInputs,
                        Terms = project.Terms!.Concat(orderingInputs.Select(name =>
                        new GqlPropertyProjectionTerm(ParseExpression(name, projectionScope), name))).ToArray()
                    };
                }
            }
            else if (clause.Word == "LET" && !final)
            {
                modifierScope = false;
                var bindings = SplitProjectionItems(body).Select(item =>
                {
                    var header = GqlRegex.Match(item, @"^\s*(?<name>" + GraphGqlIdentifiers.Pattern + @")\s*=(?!=)");
                    if (!header.Success)
                    {
                        throw Invalid("LET requires variable = expression bindings.");
                    }
                    return (Name: header.Groups["name"].Value, Expression: item[header.Length..].Trim());
                }).ToArray();
                if (bindings.Length == 0 || scope.Count + bindings.Length > 32 ||
                    bindings.Any(binding => scope.Contains(binding.Name)) ||
                    bindings.Select(binding => binding.Name).Distinct(StringComparer.Ordinal).Count() != bindings.Length)
                {
                    throw Invalid("LET requires new unique variables and at most 32 visible aliases.");
                }
                var reserved = scope.Concat(bindings.Select(binding => binding.Name)).ToHashSet(StringComparer.Ordinal);
                var terms = scope.Select(name => new GqlPropertyProjectionTerm(ParseExpression(name, scope), name)).ToList();
                foreach (var binding in bindings)
                {
                    GraphScalarExpression expression;
                    if (GqlRegex.IsMatch(MaskQuotedStrings(binding.Expression), @"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        var value = CompileValueExpression(binding.Expression, scope, snapshot, callDepth, reserved);
                        stages.Add(value);
                        reserved.Add(value.Alias!);
                        expression = new("__row", value.Alias, null);
                    }
                    else
                    {
                        expression = ParseExpression(binding.Expression, scope);
                    }
                    terms.Add(new(expression, binding.Name));
                }
                stages.Add(new("PROJECT", Terms: terms));
                scope.UnionWith(bindings.Select(binding => binding.Name));
            }
            else if (clause.Word == "UNWIND" && !final)
            {
                modifierScope = false;
                var alias = GqlRegex.Match(MaskQuotedStrings(body), @"\s+AS\s+(?<name>" + GraphGqlIdentifiers.Pattern + @")(?:\s+(?<mode>__ORDINALITY|__OFFSET)\s+(?<position>" + GraphGqlIdentifiers.Pattern + @"))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!alias.Success || scope.Count >= 32 || scope.Contains(alias.Groups["name"].Value))
                {
                    throw Invalid("FOR requires a new binding variable.");
                }
                string? positionAlias = alias.Groups["position"].Success ? alias.Groups["position"].Value : null;
                if (positionAlias is not null && (scope.Count >= 31 || scope.Contains(positionAlias) || positionAlias == alias.Groups["name"].Value))
                {
                    throw Invalid("FOR position requires a distinct new binding.");
                }
                string expression = body[..alias.Index].Trim();
                var expressionScope = scope;
                string? inputValueAlias = null;
                if (GqlRegex.IsMatch(MaskQuotedStrings(expression), @"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var value = CompileValueExpression(expression, scope, snapshot, callDepth,
                        new HashSet<string>(StringComparer.Ordinal) { alias.Groups["name"].Value });
                    stages.Add(value);
                    expression = value.Alias!;
                    inputValueAlias = value.Alias;
                    expressionScope = scope.Append(value.Alias!).ToHashSet(StringComparer.Ordinal);
                }
                stages.Add(new("UNWIND", Expression: ParseExpression(expression, expressionScope), Alias: alias.Groups["name"].Value,
                    InputValueAlias: inputValueAlias, PositionAlias: positionAlias,
                    OneBasedPosition: alias.Groups["mode"].Value.Equals("__ORDINALITY", StringComparison.OrdinalIgnoreCase)));
                scope.Add(alias.Groups["name"].Value);
                if (positionAlias is not null)
                {
                    scope.Add(positionAlias);
                }
            }
            else if (clause.Word is "WHERE" or "FILTER" && !final)
            {
                var value = GqlRegex.IsMatch(MaskQuotedStrings(body), @"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    ? CompileValueExpression(body, scope, snapshot, callDepth, scope) : null;
                var predicate = value?.Expression ?? ParseExpression(body, scope);
                if (clause.Word == "WHERE" && stages.LastOrDefault() is { MatchPlan: not null } matchStage)
                {
                    if (matchStage.Expression is not null)
                    {
                        throw Invalid("A MATCH stage accepts one WHERE clause.");
                    }
                    stages[^1] = matchStage with
                    {
                        Expression = predicate,
                        ValueSubqueries = value?.ValueSubqueries,
                        Alias = value?.Alias
                    };
                    continue;
                }
                if (clause.Word == "WHERE" && (!modifierScope || !modifiers.Add("WHERE")))
                {
                    throw Invalid("A row filter requires a preceding projection or matching stage.");
                }
                if (clause.Word == "FILTER")
                {
                    // FILTER consumes completed rows; unlike WHERE it must not
                    // become part of an OPTIONAL MATCH's null-extension logic.
                    modifierScope = false;
                }
                if (value is not null)
                {
                    stages.Add(value);
                    predicate = new("__row", value.Alias, null);
                }
                stages.Add(new(clause.Word, Expression: predicate, InputValueAlias: value?.Alias));
            }
            else if (clause.Word == "ORDER BY")
            {
                if (modifierScope && (modifiers.Count != 0 || !modifiers.Add("ORDER BY")))
                {
                    throw Invalid("ORDER BY must precede paging and WHERE in a projection stage.");
                }
                var discarded = new List<string>();
                if (orderingInputs is not null)
                {
                    scope.UnionWith(orderingInputs);
                    discarded.AddRange(orderingInputs);
                }
                var reserved = scope.ToHashSet(StringComparer.Ordinal);
                var order = SplitProjectionItems(body).Select(item =>
                {
                    var nullOrder = GqlRegex.Match(item, @"\s+NULLS\s+(FIRST|LAST)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    string ordered = nullOrder.Success ? item[..nullOrder.Index].TrimEnd() : item.TrimEnd();
                    var direction = GqlRegex.Match(ordered, @"\s+(ASC|ASCENDING|DESC|DESCENDING)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    string sourceExpression = direction.Success ? ordered[..direction.Index] : ordered;
                    GraphScalarExpression expression;
                    if (GqlRegex.IsMatch(MaskQuotedStrings(sourceExpression), @"\b(?:EXISTS|COUNT|COLLECT|VALUE)\s*\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        var value = CompileValueExpression(sourceExpression, scope, snapshot, callDepth, reserved);
                        stages.Add(value);
                        discarded.Add(value.Alias!);
                        reserved.Add(value.Alias!);
                        expression = new("__row", value.Alias, null);
                    }
                    else
                    {
                        expression = ParseExpression(sourceExpression, scope);
                    }
                    return (expression,
                        direction.Success && direction.Groups[1].Value.StartsWith("DESC", StringComparison.OrdinalIgnoreCase),
                        NullsFirst: nullOrder.Success ? (bool?)nullOrder.Groups[1].Value.Equals("FIRST", StringComparison.OrdinalIgnoreCase) : null);
                }).ToArray();
                stages.Add(new("ORDER BY", Order: order, DiscardBindings: discarded));
                if (orderingInputs is not null)
                {
                    scope.ExceptWith(orderingInputs);
                    orderingInputs = null;
                }
            }
            else if (clause.Word is "LIMIT" or "SKIP" or "OFFSET")
            {
                string modifier = clause.Word == "OFFSET" ? "SKIP" : clause.Word;
                if (modifierScope && (modifiers.Contains("WHERE") || !modifiers.Add(modifier)))
                {
                    throw Invalid("Paging clauses must be unique within a projection stage.");
                }
                if (!Int32.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                {
                    throw Invalid("Row paging requires a non-negative integer.");
                }
                stages.Add(new(clause.Word == "LIMIT" ? "LIMIT" : "SKIP", Count: count, ProjectionModifier: modifierScope));
            }
            else
            {
                throw Invalid($"{clause.Word} is not supported in this row pipeline.");
            }
        }
        // Paging syntax may spell LIMIT before OFFSET; its meaning is always
        // offset first, then limit within the same stage.
        for (int i = 0; i + 1 < stages.Count; i++)
        {
            if (stages[i] is { Operation: "LIMIT", ProjectionModifier: true } &&
                stages[i + 1] is { Operation: "SKIP", ProjectionModifier: true })
            {
                (stages[i], stages[i + 1]) = (stages[i + 1], stages[i]);
            }
        }
        bool entityGraph = explicitProductGraph || !singleton && !clauses.Any(clause => clause.Word is "WITH" or "UNWIND" or "LET" or "CALL") &&
            stages.LastOrDefault(stage => stage.Operation == "PROJECT") is { Aggregation: null, Terms: { } finalTerms } &&
            finalTerms.All(term => term.Expression.Binding == "__row" && term.Expression.Function is null);
        return seed with
        {
            Pipeline = new(stages, singleton),
            ReturnsEntityGraph = entityGraph
        };
    }

}
