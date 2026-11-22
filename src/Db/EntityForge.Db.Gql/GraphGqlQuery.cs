using EntityForge.Db.Query.Syntax.Gql;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EntityForge.Query.Querying;
using EntityForge.Query.Projection;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

/// <summary>Requests GQL execution against a native Graph database or a read-only Semantic evidence view.</summary>
/// <param name="Query">Graph GQL query text.</param>
/// <param name="SnapshotVersion">Retained version in the selected graph, or null to acquire its current published snapshot. An unavailable version fails; it does not fall back to newer data.</param>
/// <param name="IncludeSupportingGraph">Whether to include supporting entities in the ordinary query response when the result supports them. The scan endpoint requires false.</param>
/// <param name="Parameters">Named JSON scalar, list or map values for $name placeholders outside quoted strings. Missing names are rejected; collections may nest at most eight levels within expression and query limits.</param>
/// <remarks>
/// This is EntityForge's HTTP request contract, not a standard GQL wire protocol.
/// The ordinary query endpoint buffers its bounded response and holds a snapshot lease
/// during evaluation. A returned version identifies the snapshot but does not retain it
/// for a subsequent request. Versions are scoped to the selected physical graph.
/// The scan endpoint accepts a narrower set of execution plans and streams bindings
/// without a supporting graph. Consumers must observe its final completion frame;
/// receiving pages or reaching the end of a broken connection does not establish success.
/// Query text is validated against the supported standard syntax and execution surface;
/// constructing this request does not validate or execute it.
/// The Semantic view endpoint accepts current-state reads only and rejects SnapshotVersion.
/// Its field assertions are edges to typed value vertices rather than scalar entity properties.
/// </remarks>
public sealed record GraphGqlQueryRequest(string Query, long? SnapshotVersion = null, bool IncludeSupportingGraph = false,
    IReadOnlyDictionary<string, JsonElement>? Parameters = null);

internal sealed partial record CompiledGraphGqlQuery(BoundedGraphPatternQuery Query, long? SnapshotVersion,
    GqlPropertyProjection? Projection = null, GqlRowPipeline? Pipeline = null, GqlUnion? Union = null)
{
    internal bool RequiresCompleteGraph => ClockRequest is not null || ClockBound || Union is not null || Pipeline?.Stages.Any(stage => stage.MatchPlan is not null || stage.CallPlan is not null || stage.ValueSubqueries?.Count > 0) == true;

    internal bool ReturnsEntityGraph
    {
        get; init;
    }

    internal IReadOnlyList<EntityForge.Db.Graph.Contracts.GraphVertexId>? StartCandidates
    {
        get; init;
    }

    internal IReadOnlySet<string>? GroupRelationshipBindings
    {
        get; init;
    }

    internal IReadOnlyDictionary<string, string>? OutputNames
    {
        get; init;
    }

    internal GraphGqlQueryRequest? ClockRequest
    {
        get; init;
    }

    internal bool ClockPatternOnly
    {
        get; init;
    }

    internal GraphPatternPathMode ClockPathMode
    {
        get; init;
    }

    internal GraphPatternPathSelection ClockWalkSelection
    {
        get; init;
    }

    internal int ClockWalkSelectionCount { get; init; } = 1;

    internal bool ClockBound
    {
        get; init;
    }

    internal CompiledGraphGqlQuery BindExecutionClock() => ClockRequest is { } request
        ? new GraphGqlQueryCompiler(DateTimeOffset.UtcNow).Compile(request, ClockPatternOnly, ClockPathMode, ClockWalkSelection, ClockWalkSelectionCount) with
        {
            ClockBound = true,
            OmitsResult = OmitsResult
        } : this;

    internal async ValueTask<QueryResultEnvelope> ExecuteRowsAsync(QueryResultEnvelope input,
        Func<CompiledGraphGqlQuery, ValueTask<QueryResultEnvelope>> execute, CancellationToken cancellation,
        GqlExecutionBudget budget, IReadOnlyDictionary<string, QueryValue>? retainedImports)
    {
        if (ClockRequest is not null)
        {
            return await BindExecutionClock().ExecuteRowsAsync(input, execute, cancellation, budget, retainedImports).ConfigureAwait(false);
        }
        var result = Union is { } union
            ? await union.ExecuteAsync(execute, cancellation, budget, tableInput: input, retainedImports: retainedImports).ConfigureAwait(false)
            : await new GqlRowPipeline(Pipeline!.Stages).ApplyAsync(input, execute, cancellation, budget,
                retainedImports: retainedImports).ConfigureAwait(false);
        return ApplyOutputNames(result);
    }

    internal async ValueTask<EntityForge.Query.Results.QueryResultEnvelope> ExecuteAsync(
        Func<CompiledGraphGqlQuery, ValueTask<EntityForge.Query.Results.QueryResultEnvelope>> execute, CancellationToken cancellation,
        GqlExecutionBudget? budget = null, GraphPatternMatch? seed = null, GraphResult? seedGraph = null)
    {
        if (ClockRequest is not null)
        {
            // Capture at execution, never in the reusable parsed plan. Literal
            // binding keeps all row stages on one captured statement clock.
            return await BindExecutionClock()
                .ExecuteAsync(execute, cancellation, budget, seed, seedGraph).ConfigureAwait(false);
        }
        budget ??= new GqlExecutionBudget();
        if (Union is { } union)
        {
            return ApplyOutputNames(await union.ExecuteAsync(execute, cancellation, budget, seed, seedGraph).ConfigureAwait(false));
        }
        var result = await execute(this).ConfigureAwait(false);
        if (seedGraph is not null && result.Result is GraphPatternResult selected)
        {
            result = new(result.QueryId, new GraphPatternResult(new GraphResult(
                seedGraph.Nodes.Concat(selected.Graph.Nodes).DistinctBy(node => node.Identity),
                seedGraph.Edges.Concat(selected.Graph.Edges).DistinctBy(edge => edge.Identity)), selected.Matches),
                result.CorrelationId, result.Evidence, result.SemanticModels, result.Diagnostics);
        }
        result = Pipeline is null ? result : await Pipeline.ApplyAsync(result, execute, cancellation, budget, seed).ConfigureAwait(false);
        int rowCount = result.Result switch
        {
            GraphPatternResult matched => matched.Matches.Count,
            RecordResult records => records.Rows.Count,
            _ => 0
        };
        if (rowCount > GraphGqlQueryCompiler.MaximumAggregateInputRows)
        {
            throw new ArgumentException("GQL result exceeds 10,000 rows; use an explicit LIMIT to bound the result.");
        }
        if (ReturnsEntityGraph && result.Result is GraphPatternResult graph)
        {
            var values = graph.Matches.SelectMany(row => row.Bindings.Values).ToArray();
            var nodes = values.OfType<NodeValue>().Select(node => node.Identity).ToHashSet();
            var edges = values.OfType<EdgeValue>().Select(edge => edge.Identity).ToHashSet();
            var identifiers = nodes.Concat(edges).OfType<IdentifierValue>().Select(id => id.Identifier.StartsWith("urn:uuid:", StringComparison.Ordinal)
                ? id.Identifier[9..] : id.Identifier).ToHashSet(StringComparer.Ordinal);
            result = new(result.QueryId, new GraphPatternResult(new GraphResult(
                graph.Graph.Nodes.Where(node => nodes.Contains(node.Identity)),
                graph.Graph.Edges.Where(edge => edges.Contains(edge.Identity))), graph.Matches),
                result.CorrelationId, result.Evidence.Where(item => item.SourceRecordIdentifier is { } id && identifiers.Contains(id)), result.SemanticModels, result.Diagnostics);
        }
        return ApplyOutputNames(result);
    }
}

internal sealed record GqlPropertyProjection(IReadOnlyList<GqlPropertyProjectionTerm> Terms);

internal sealed record GqlPropertyProjectionTerm(GraphScalarExpression Expression, string Alias)
{
    public IEnumerable<string> Bindings => Expression.Bindings;
}

internal sealed partial class GraphGqlQueryCompiler
{
    internal static GraphScalarExpression ParseExpression(string source, IReadOnlySet<string>? rowScope = null) => new ProjectionExpressionParser(source, rowScope).Parse();

    internal const int MaximumQueryCharacters = GqlSyntaxParser.MaximumQueryCharacters;
    internal const int MaximumPatternClauses = 16;
    internal const int MaximumPredicates = 32;
    internal const int MaximumAggregateInputRows = 10_000;
    private const string InlinePropertyMap = @"(?:\{(?<properties>(?:""(?:\\.|[^""\\])*""|[^{}""]|\{(?<mapDepth>)|\}(?<-mapDepth>))*)(?(mapDepth)(?!))\}\s*)?";

    private static readonly Regex Node = new(@"\G\s*\(\s*(?<binding>" + GraphGqlIdentifiers.Pattern + @")?(?:\s*[:&]\s*(?<label>" + GraphGqlIdentifiers.NamePattern + @"))*\s*" + InlinePropertyMap + @"\)",
        RegexOptions.CultureInvariant);

    private const string RelationshipBody = @"(?:\[\s*(?<binding>" + GraphGqlIdentifiers.Pattern + @")?(?::(?<label>" + GraphGqlIdentifiers.NamePattern + @"))?\s*(?<legacy>\*\s*(?<legacyMin>[0-9]+)?\s*(?:(?<legacyRange>\.\.)\s*(?<legacyMax>[0-9]+)?)?\s*)?" + InlinePropertyMap + @"\])?";

    private static readonly Regex Outgoing = new(@"\G\s*-" + RelationshipBody + @"->",
        RegexOptions.CultureInvariant);

    private static readonly Regex Incoming = new(@"\G\s*<-" + RelationshipBody + @"-",
        RegexOptions.CultureInvariant);

    private static readonly Regex Undirected = new(@"\G\s*-" + RelationshipBody + @"-",
        RegexOptions.CultureInvariant);

    private static readonly Regex Quantifier = new(
        @"^\s*(?:(?<plus>[+*])|\{(?<minimum>[0-9]*)(?:,(?<maximum>[0-9]*)?)?\})",
        RegexOptions.CultureInvariant);

    private const string PageClause = @"(?:(?:\s+LIMIT\s+(?<limit>[0-9]+))(?:\s+(?:OFFSET|SKIP)\s+(?<offset>[0-9]+))?|(?:\s+(?:OFFSET|SKIP)\s+(?<offset>[0-9]+))(?:\s+LIMIT\s+(?<limit>[0-9]+))?)?\s*;?\s*$";

    private static readonly Regex Return = new(
        @"^RETURN\s+(?:(?:(?<distinct>DISTINCT)|ALL)\s+)?(?<projection>GRAPH|\*|" + GraphGqlIdentifiers.Pattern + @"(?:\s*,\s*" + GraphGqlIdentifiers.Pattern + @")*)" +
        @"(?:\s+ORDER\s+BY\s+(?<order>" + GraphGqlIdentifiers.Pattern + @"\." + GraphGqlIdentifiers.Pattern +
        @"(?:\s+(?:ASC|DESC))?(?:\s*,\s*" + GraphGqlIdentifiers.Pattern + @"\." + GraphGqlIdentifiers.Pattern +
        @"(?:\s+(?:ASC|DESC))?)*))?" + PageClause,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PropertyReturn = new(
        @"^RETURN\s+(?:(?:(?<distinct>DISTINCT)|ALL)\s+)?(?<items>.+?\s+AS\s+" + GraphGqlIdentifiers.Pattern + @"(?:\s*,\s*.+?\s+AS\s+" + GraphGqlIdentifiers.Pattern + @")*)(?:\s+ORDER\s+BY\s+(?<order>" + GraphGqlIdentifiers.Pattern + @"(?:\s+(?:ASC|DESC))?" +
        @"(?:\s*,\s*" + GraphGqlIdentifiers.Pattern + @"(?:\s+(?:ASC|DESC))?)*))?" + PageClause,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex OrderTerm = new(
        @"^(?<binding>" + GraphGqlIdentifiers.Pattern + @")\.(?<property>" + GraphGqlIdentifiers.Pattern + @")" +
        @"(?:\s+(?<direction>ASC|DESC))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PropertyReference = new(
        @"^(?<binding>" + GraphGqlIdentifiers.Pattern + @")\.(?<property>" + GraphGqlIdentifiers.Pattern + @")$",
        RegexOptions.CultureInvariant);

    public CompiledGraphGqlQuery Compile(GraphGqlQueryRequest request, bool patternOnly = false, GraphPatternPathMode pathMode = GraphPatternPathMode.Walk,
        GraphPatternPathSelection singleWalkSelection = GraphPatternPathSelection.All, int singleWalkSelectionCount = 1)
    {
        ArgumentNullException.ThrowIfNull(request);
        string source = request.Query?.Trim() ?? String.Empty;
        if (source.Length > MaximumQueryCharacters)
        {
            throw Invalid($"A GQL query cannot exceed {MaximumQueryCharacters:N0} characters.");
        }
        var prepared = PrepareImplicitColumns(source);
        string bound = BindStatementClock(prepared.Source, _statementTime ?? DateTimeOffset.UnixEpoch);
        var compiled = CompileCore(request with
        {
            Query = bound
        }, patternOnly, pathMode, singleWalkSelection, singleWalkSelectionCount);
        if (_statementTime is null && bound != prepared.Source)
        {
            compiled = compiled with
            {
                ClockRequest = request,
                ClockPatternOnly = patternOnly,
                ClockPathMode = pathMode,
                ClockWalkSelection = singleWalkSelection,
                ClockWalkSelectionCount = singleWalkSelectionCount
            };
        }
        return prepared.Names.Count == 0 ? compiled : compiled with
        {
            OutputNames = prepared.Names
        };
    }

    private CompiledGraphGqlQuery CompileCore(GraphGqlQueryRequest request, bool patternOnly, GraphPatternPathMode pathMode,
        GraphPatternPathSelection singleWalkSelection, int singleWalkSelectionCount)
    {
        ArgumentNullException.ThrowIfNull(request);
        string source = request.Query?.Trim() ?? String.Empty;
        if (source.Length > MaximumQueryCharacters)
        {
            throw Invalid($"A GQL query cannot exceed {MaximumQueryCharacters:N0} characters.");
        }

        source = NormalizeQuotedStrings(source);
        source = BindParameters(source, request.Parameters, allowCollections: true);
        source = LowerPatternComprehensions(source);
        if (TryCompileUnion(source, request.SnapshotVersion) is { } union)
        {
            return union;
        }
        source = LowerFor(source);
        source = LowerAggregateExpressions(source);
        if (!patternOnly && TryCompileRowPipeline(source, request.SnapshotVersion) is { } staged)
        {
            return staged;
        }

        Match matchKeyword = GqlRegex.Match(source, @"^MATCH\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!matchKeyword.Success)
        {
            throw Invalid("A GQL query must begin with MATCH.");
        }

        Match returnBoundary = GqlRegex.Match(MaskQuotedStrings(source), @"\s+(?=RETURN\s+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!returnBoundary.Success)
        {
            throw Invalid("A graph query requires a RETURN projection or FINISH.");
        }

        string matchAndWhere = source[matchKeyword.Length..returnBoundary.Index].Trim();
        bool repeatable = ReadMatchMode(ref matchAndWhere);
        GraphPatternPathSelection pathSelection = GraphPatternPathSelection.All;
        const string anyShortest = "ANY SHORTEST ";
        const string allShortest = "ALL SHORTEST ";
        if (matchAndWhere.StartsWith(allShortest, StringComparison.OrdinalIgnoreCase))
        {
            pathSelection = GraphPatternPathSelection.AllShortest;
            matchAndWhere = matchAndWhere[allShortest.Length..].TrimStart();
        }
        else if (matchAndWhere.StartsWith(anyShortest, StringComparison.OrdinalIgnoreCase))
        {
            pathSelection = GraphPatternPathSelection.AnyShortest;
            matchAndWhere = matchAndWhere[anyShortest.Length..].TrimStart();
        }
        string returnClause = source[(returnBoundary.Index + returnBoundary.Length)..];
        GraphPatternAggregation? aggregate = ParseAggregateReturn(returnClause);
        bool hasAggregate = aggregate is not null;
        Match propertyReturn = PropertyReturn.Match(returnClause);
        Match returned = hasAggregate ? Return.Match("RETURN *") : propertyReturn.Success
            ? propertyReturn : Return.Match(returnClause);
        if (!returned.Success)
        {
            throw Invalid(
            "This slice supports graph/binding projections, scalar expressions and bounded grouped aggregates.");
        }

        int maximumMatches = hasAggregate ? MaximumAggregateInputRows + 1 : returned.Groups["limit"].Success
            ? Int32.Parse(returned.Groups["limit"].Value, CultureInfo.InvariantCulture) : MaximumAggregateInputRows + 1;
        GqlPropertyProjection? propertyProjection = !hasAggregate && propertyReturn.Success
            ? ParsePropertyProjection(propertyReturn) : null;
        string[]? returnedBindings = hasAggregate
            ? AggregateBindings(aggregate!).Distinct(StringComparer.Ordinal).ToArray()
            : propertyProjection is not null
                ? propertyProjection.Terms.SelectMany(term => term.Bindings).Distinct(StringComparer.Ordinal).ToArray()
            : returned.Groups["projection"].Value.Equals("GRAPH", StringComparison.OrdinalIgnoreCase) ? null
                : returned.Groups["projection"].Value.Split(',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        GraphPatternOrderTerm[] orderBy = propertyProjection is not null && returned.Groups["order"].Success
            ? returned.Groups["order"].Value.Split(',', StringSplitOptions.TrimEntries)
                .Select(term => PropertyOrder(propertyProjection, term)).ToArray()
            : !hasAggregate && returned.Groups["order"].Success
            ? returned.Groups["order"].Value.Split(',', StringSplitOptions.TrimEntries)
                .Select(ParseOrderTerm).ToArray() : [];
        bool distinct = !hasAggregate && returned.Groups["distinct"].Success;
        if (distinct && returned.Groups["projection"].Value.Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("DISTINCT requires an explicit binding projection.");
        }

        int offset = !hasAggregate && returned.Groups["offset"].Success
            ? Int32.Parse(returned.Groups["offset"].Value, CultureInfo.InvariantCulture) : 0;
        string patternText = matchAndWhere;
        string? whereText = null;
        Match whereBoundary = GqlRegex.Match(MaskQuotedStrings(matchAndWhere), @"\s+(?=WHERE\s+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (whereBoundary.Success)
        {
            patternText = matchAndWhere[..whereBoundary.Index];
            whereText = matchAndWhere[(whereBoundary.Index + whereBoundary.Length)..][5..].Trim();
        }

        MatchCollection optionalBoundaries = GqlRegex.Matches(MaskQuotedStrings(patternText), @"\s+OPTIONAL\s+MATCH\s+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var patternGroupList = new List<string>();
        int groupStart = 0;
        foreach (Match boundary in optionalBoundaries)
        {
            patternGroupList.Add(patternText[groupStart..boundary.Index]);
            groupStart = boundary.Index + boundary.Length;
        }
        patternGroupList.Add(patternText[groupStart..]);
        string[] patternGroups = patternGroupList.ToArray();
        string[] clauses = SplitPatternClauses(patternGroups[0]).ToArray();
        if (clauses.Length == 0)
        {
            throw Invalid("MATCH requires a named start node.");
        }

        int clauseCount = clauses.Length + patternGroups.Skip(1).Sum(group => SplitPatternClauses(group).Count());
        if (clauseCount > MaximumPatternClauses)
        {
            throw Invalid($"A GQL query cannot exceed {MaximumPatternClauses} pattern clauses.");
        }

        var nodes = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
        var rawRelationships = new List<(string Binding, string SourceBinding, string? Label,
            GraphPatternDirection Direction, string TargetBinding, GraphPatternMatchKind MatchKind,
            GraphPatternHopRange Hops, IReadOnlyList<GraphPatternPropertyPredicate> Predicates, bool Grouped)>();
        string? startBinding = null;
        for (int clauseIndex = 0; clauseIndex < clauses.Length; clauseIndex++)
        {
            ParseClause(clauses[clauseIndex], clauseIndex == 0, GraphPatternMatchKind.Required,
                nodes, rawRelationships, ref startBinding);
        }

        foreach (string optionalGroup in patternGroups.Skip(1))
        {
            int firstOptionalRelationship = rawRelationships.Count;
            foreach (string optionalClause in SplitPatternClauses(optionalGroup))
            {
                ParseClause(optionalClause, false, GraphPatternMatchKind.Optional,
                    nodes, rawRelationships, ref startBinding);
            }
            if (rawRelationships.Count - firstOptionalRelationship != 1)
            {
                throw new NotSupportedException("Each OPTIONAL MATCH currently supports one relationship pattern; use separate optional clauses for independent optional matches.");
            }
        }

        bool optionalWhere = whereText is not null && patternGroups.Length > 1;
        if (optionalWhere && (rawRelationships.Count(item => item.MatchKind == GraphPatternMatchKind.Optional) != 1 ||
            !rawRelationships[^1].Hops.IsFixed))
        {
            throw new NotSupportedException("WHERE on OPTIONAL MATCH currently requires one fixed optional relationship.");
        }
        int inlinePredicateCount = nodes.Values.Sum(node => node.Predicates.Count) + rawRelationships.Sum(item => item.Predicates.Count);
        if (inlinePredicateCount > MaximumPredicates)
        {
            throw Invalid($"A GQL query cannot exceed {MaximumPredicates} predicates.");
        }
        GraphPatternFilter? filter = null;
        if (whereText is not null)
        {
            try
            {
                var predicateParser = new PredicateExpressionParser(whereText);
                filter = predicateParser.Parse();
                if (predicateParser.PredicateCount + inlinePredicateCount > MaximumPredicates)
                {
                    throw Invalid($"A GQL query cannot exceed {MaximumPredicates} predicates.");
                }
            }
            catch (ArgumentException)
            {
                GraphScalarExpression expression = ParseExpression(whereText);
                if (CountExpressionPredicates(expression) + inlinePredicateCount > MaximumPredicates)
                {
                    throw Invalid($"A GQL query cannot exceed {MaximumPredicates} predicates.");
                }
                filter = new GraphPatternExpressionFilter(expression);
            }

            if (!optionalWhere && TryConjunctiveComparisons(filter, out IReadOnlyList<GraphPatternPropertyComparison>? comparisons))
            {
                foreach (GraphPatternPropertyComparison comparison in comparisons)
                {
                    if (nodes.TryGetValue(comparison.Binding, out MutableNode? node))
                    {
                        node.Predicates.Add(new(comparison.Property, comparison.Value, comparison.Operator));
                    }
                }
            }
        }

        GraphPatternNode start = Freeze(nodes[startBinding!]);
        if (aggregate is null && propertyProjection is null &&
            (returnedBindings is null || returned.Groups["projection"].Value == "*"))
        {
            returnedBindings = nodes.Keys.Concat(rawRelationships.Select(item => item.Binding))
                .Where(binding => !binding.StartsWith('$')).ToArray();
            if (returnedBindings.Length == 0)
            {
                throw Invalid("RETURN * requires at least one named binding; an anonymous pattern can use COUNT(*).");
            }
        }
        if (pathSelection is not GraphPatternPathSelection.All && rawRelationships.Count != 1)
        {
            throw Invalid("This slice applies a shortest-path selector to one quantified relationship pattern.");
        }

        GraphPatternFilter? Combined(GraphPatternFilter? first, GraphPatternFilter? second) =>
            first is null ? second : second is null ? first : new GraphPatternBooleanFilter(first, GraphPatternBooleanOperator.And, second);
        GraphPatternFilter? EdgeFilter(string binding, IReadOnlyList<GraphPatternPropertyPredicate> predicates) =>
            predicates.Select(p => RequiresScalarEquality(p.Value) ? ScalarEquality(binding, p.Property, p.Value) :
                (GraphPatternFilter)new GraphPatternPropertyComparison(binding, p.Property, p.Value, p.Operator))
                .Aggregate((GraphPatternFilter?)null, Combined);
        GraphPatternFilter? NodeScalarFilter(string binding) => EdgeFilter(binding,
            nodes[binding].Predicates.Where(p => RequiresScalarEquality(p.Value)).ToArray());
        // For repeatable WALK, replacing a segment by a shorter segment with
        // the same endpoints preserves the surrounding chain. Retain k local
        // paths/length groups per endpoint before selecting complete paths.
        // Other path modes can impose cross-segment uniqueness constraints.
        bool selectWalkSegments = singleWalkSelection != GraphPatternPathSelection.All &&
            repeatable && pathMode == GraphPatternPathMode.Walk;
        GraphPatternRelationship[] relationships = rawRelationships.Select(item => new GraphPatternRelationship(
            item.Binding, item.SourceBinding, item.Label, item.Direction, Freeze(nodes[item.TargetBinding]),
            item.MatchKind, item.Hops, selectWalkSegments && item.Hops.Maximum is null ? singleWalkSelection : pathSelection,
            item.MatchKind == GraphPatternMatchKind.Optional ? Combined(NodeScalarFilter(item.TargetBinding),
                Combined(optionalWhere ? filter : null, EdgeFilter(item.Binding, item.Hops.IsFixed ? item.Predicates : []))) : null,
            item.Hops.IsFixed ? [] : item.Predicates, repeatable,
            // Every contiguous segment of a TRAIL, SIMPLE or ACYCLIC path
            // satisfies that mode itself. Apply this necessary constraint
            // during traversal, including unbounded repeatable segments; the
            // whole-path binding still checks uniqueness across segments.
            pathMode, selectWalkSegments && item.Hops.Maximum is null ? singleWalkSelectionCount : 1)).ToArray();
        if (optionalWhere)
        {
            filter = null;
        }
        filter = Combined(filter, NodeScalarFilter(startBinding!));
        foreach (var item in rawRelationships.Where(item => item.MatchKind == GraphPatternMatchKind.Required))
        {
            filter = Combined(filter, EdgeFilter(item.Binding, item.Hops.IsFixed ? item.Predicates : []));
            filter = Combined(filter, NodeScalarFilter(item.TargetBinding));
        }
        orderBy = orderBy.Select(term => term.Expression is null
            ? new GraphPatternOrderTerm(new GraphScalarExpression(term.Binding, term.Property, null), term.Direction)
            : term).ToArray();
        if ((aggregate is not null || propertyProjection is not null) && returnedBindings?.Length == 0)
        {
            returnedBindings = [startBinding!];
        }

        return new(new(start, relationships, maximumMatches,
            maximumExpansions: hasAggregate ? MaximumAggregateInputRows + 1 :
                EntityTraversalLimits.DefaultMaximumExpandedEntities,
            returnedBindings: returnedBindings, orderBy: orderBy, distinct: distinct, offset: offset, filter: filter,
            aggregation: aggregate),
            request.SnapshotVersion, propertyProjection)
        {
            GroupRelationshipBindings = rawRelationships.Where(item => item.Grouped).Select(item => item.Binding).ToHashSet(StringComparer.Ordinal)
        };
    }

    private static GqlPropertyProjection ParsePropertyProjection(Match match)
    {
        GqlPropertyProjectionTerm[] terms = SplitProjectionItems(match.Groups["items"].Value).Select(item =>
            {
                Match term = GqlRegex.Match(MaskQuotedStrings(item), @"\s+AS\s+(?<alias>" + GraphGqlIdentifiers.Pattern + @")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!term.Success)
                {
                    throw Invalid($"Unsupported property projection '{item}'.");
                }

                return new GqlPropertyProjectionTerm(new ProjectionExpressionParser(item[..term.Index]).Parse(), term.Groups["alias"].Value);
            }).ToArray();
        if (terms.Select(term => term.Alias).Distinct(StringComparer.Ordinal).Count() != terms.Length)
        {
            throw Invalid("Property projection aliases must be unique.");
        }

        return new(terms);
    }

    private static int CountExpressionPredicates(GraphScalarExpression expression) =>
        (expression.Function is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "ISNULL" or "ISNOTNULL" or "ISNORMALIZED" or "ISNOTNORMALIZED" or "ISTYPED" or "ISNOTTYPED" or "STARTSWITH" or "ENDSWITH" or "CONTAINS" ? 1 : 0) +
        (expression.Arguments?.Sum(CountExpressionPredicates) ?? 0);

    private static GraphScalarExpression ParseProjectionOperand(string source)
    {
        Match property = PropertyReference.Match(source);
        if (property.Success)
        {
            return new(property.Groups["binding"].Value,
            property.Groups["property"].Value, null);
        }

        if (source.StartsWith('"') && source.EndsWith('"'))
        {
            return new(null, null, Literal(source));
        }

        if (Int64.TryParse(source, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
        {
            return new(null, null, new LiteralValue(integer.ToString(CultureInfo.InvariantCulture),
                QueryScalarKind.Integer));
        }

        if (Double.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return new(null, null, Literal(source));
        }

        throw Invalid($"Unsupported projection operand '{source}'.");
    }

    private static GraphPatternOrderTerm PropertyOrder(GqlPropertyProjection projection, string source)
    {
        string[] parts = source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string alias = parts[0];
        GraphPatternSortDirection direction = parts.Length > 1 && parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase)
            ? GraphPatternSortDirection.Descending : GraphPatternSortDirection.Ascending;
        GqlPropertyProjectionTerm term = projection.Terms.SingleOrDefault(item => item.Alias == alias)
            ?? throw Invalid($"ORDER BY references unknown projection alias '{alias}'.");
        if (term.Expression.Function is not null || term.Expression.Binding is null ||
            term.Expression.Property is null)
        {
            return new(term.Expression, direction);
        }

        return new(term.Expression.Binding, term.Expression.Property, direction);
    }

    private static GraphPatternOrderTerm ParseOrderTerm(string value)
    {
        Match match = OrderTerm.Match(value);
        if (!match.Success)
        {
            throw Invalid($"Unsupported ORDER BY term '{value}'.");
        }

        return new(match.Groups["binding"].Value, match.Groups["property"].Value,
            match.Groups["direction"].Value.Equals("DESC", StringComparison.OrdinalIgnoreCase)
                ? GraphPatternSortDirection.Descending : GraphPatternSortDirection.Ascending);
    }

    private static GraphPatternComparisonOperator Comparison(string value) => value switch
    {
        "=" => GraphPatternComparisonOperator.Equal,
        "<>" or "!=" => GraphPatternComparisonOperator.NotEqual,
        "<" => GraphPatternComparisonOperator.LessThan,
        "<=" => GraphPatternComparisonOperator.LessThanOrEqual,
        ">" => GraphPatternComparisonOperator.GreaterThan,
        ">=" => GraphPatternComparisonOperator.GreaterThanOrEqual,
        _ => throw Invalid($"Unsupported comparison operator '{value}'.")
    };

    private static void ParseClause(string clause, bool firstClause, GraphPatternMatchKind matchKind,
        IDictionary<string, MutableNode> nodes,
        ICollection<(string Binding, string SourceBinding, string? Label,
            GraphPatternDirection Direction, string TargetBinding, GraphPatternMatchKind MatchKind,
            GraphPatternHopRange Hops, IReadOnlyList<GraphPatternPropertyPredicate> Predicates, bool Grouped)> relationships,
        ref string? startBinding)
    {
        int position = 0;
        Match sourceMatch = Node.Match(clause, position);
        if (!sourceMatch.Success)
        {
            throw Invalid("Every MATCH pattern must begin with a named node.");
        }

        string sourceBinding = Value(sourceMatch, "binding") ?? "$anonNode" + nodes.Count;
        if (firstClause)
        {
            MutableNode source = Mutable(sourceMatch, sourceBinding);
            nodes.Add(sourceBinding, source);
            startBinding = sourceBinding;
        }
        else
        {
            if (!nodes.TryGetValue(sourceBinding, out MutableNode? existing))
            {
                throw Invalid($"A branch must begin at an already-bound node; '{sourceBinding}' is unbound.");
            }

            ValidateOptionalConstraints(existing, sourceMatch, matchKind);
            MergeLabels(existing, sourceMatch);
        }
        position = sourceMatch.Index + sourceMatch.Length;
        if (position == clause.Length && !firstClause)
        {
            throw Invalid("A branch must introduce at least one relationship.");
        }

        while (position < clause.Length)
        {
            Match edge = Outgoing.Match(clause, position);
            GraphPatternDirection direction = GraphPatternDirection.Outgoing;
            if (!edge.Success)
            {
                edge = Incoming.Match(clause, position);
                direction = GraphPatternDirection.Incoming;
            }
            if (!edge.Success)
            {
                edge = Undirected.Match(clause, position);
                direction = GraphPatternDirection.Undirected;
            }
            if (!edge.Success)
            {
                throw Invalid($"Unsupported relationship pattern near '{clause[position..]}'.");
            }

            position = edge.Index + edge.Length;
            Match quantifier = Quantifier.Match(clause[position..]);
            bool legacy = edge.Groups["legacy"].Success;
            if (legacy && quantifier.Success)
            {
                throw Invalid("A relationship cannot combine legacy and postfix quantifiers.");
            }
            if (quantifier.Success)
            {
                position += quantifier.Length;
            }

            Match targetMatch = Node.Match(clause, position);
            if (!targetMatch.Success)
            {
                throw Invalid("Every relationship must terminate at a named node.");
            }

            MutableNode parsedTarget = Mutable(targetMatch, Value(targetMatch, "binding") ?? "$anonNode" + nodes.Count);
            if (nodes.TryGetValue(parsedTarget.Binding, out MutableNode? target))
            {
                ValidateOptionalConstraints(target, targetMatch, matchKind);
                MergeLabels(target, targetMatch);
            }
            else
            {
                target = parsedTarget;
                nodes.Add(target.Binding, target);
            }
            GraphPatternHopRange hops = legacy ? LegacyHops(edge) : quantifier.Success
                ? QuantifiedHops(quantifier)
                : GraphPatternHopRange.Fixed;
            var predicates = InlinePredicates(edge).ToArray();
            relationships.Add((Value(edge, "binding") ?? "$anonEdge" + relationships.Count, sourceBinding, Value(edge, "label"),
                direction, target.Binding, matchKind, hops, predicates, legacy || quantifier.Success));
            sourceBinding = target.Binding;
            position = targetMatch.Index + targetMatch.Length;
        }
    }

    private static GraphPatternHopRange LegacyHops(Match edge)
    {
        int Number(string name, int fallback) => edge.Groups[name].Length == 0 ? fallback :
            Int32.TryParse(edge.Groups[name].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                ? number : throw Invalid("The relationship hop bound is too large.");
        int minimum = Number("legacyMin", 1);
        int? maximum = edge.Groups["legacyRange"].Success
            ? edge.Groups["legacyMax"].Length == 0 ? null : Number("legacyMax", 0)
            : edge.Groups["legacyMin"].Length == 0 ? null : minimum;
        return new(minimum, maximum);
    }

    private static GraphPatternHopRange QuantifiedHops(Match quantifier)
    {
        if (quantifier.Groups["plus"].Success)
        {
            return new(quantifier.Groups["plus"].Value == "*" ? 0 : 1, null);
        }

        if (quantifier.Groups["minimum"].Length == 0 && !quantifier.Value.Contains(','))
        {
            throw Invalid("A quantified range requires a bound or comma.");
        }
        int minimum = quantifier.Groups["minimum"].Length == 0 ? 0 :
            Int32.Parse(quantifier.Groups["minimum"].Value, CultureInfo.InvariantCulture);
        int? maximum = quantifier.Value.Contains(',') && quantifier.Groups["maximum"].Length == 0 ? null :
            quantifier.Groups["maximum"].Success
            ? Int32.Parse(quantifier.Groups["maximum"].Value, CultureInfo.InvariantCulture)
            : minimum;
        return new(minimum, maximum);
    }

    private static void ValidateOptionalConstraints(MutableNode node, Match match, GraphPatternMatchKind matchKind)
    {
        if (matchKind == GraphPatternMatchKind.Optional &&
            (InlinePredicates(match).Any() || match.Groups["label"].Captures.Any(capture => !node.Labels.Contains(GraphGqlIdentifiers.DecodeName(capture.Value)))))
        {
            throw new NotSupportedException("OPTIONAL MATCH cannot add inline constraints to an already-bound node.");
        }
    }

    private static void MergeLabels(MutableNode node, Match match)
    {
        foreach (Capture capture in match.Groups["label"].Captures)
        {
            string label = GraphGqlIdentifiers.DecodeName(capture.Value);
            if (!node.Labels.Contains(label, StringComparer.Ordinal))
            {
                node.Labels.Add(label);
            }
        }
        node.Predicates.AddRange(InlinePredicates(match));
    }

    private static QueryValue Literal(string text)
    {
        text = text.Trim();
        if (text.StartsWith('"'))
        {
            try
            {
                return new LiteralValue(JsonSerializer.Deserialize<string>(text)!);
            }
            catch (JsonException exception)
            {
                throw Invalid("A quoted GQL literal is invalid.", exception);
            }
        }
        if (Boolean.TryParse(text, out bool boolean))
        {
            return new LiteralValue(boolean ? "true" : "false", QueryScalarKind.Boolean);
        }

        if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
        {
            return new LiteralValue(integer.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }

        if (!text.Contains('e', StringComparison.OrdinalIgnoreCase) &&
            GqlRegex.IsMatch(text, @"^[+-]?[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant))
        {
            string digits = new(text.Where(Char.IsAsciiDigit).ToArray());
            int dot = text.IndexOf('.');
            int scale = dot < 0 ? 0 : text[(dot + 1)..].TrimEnd('0').Length;
            if (digits.TrimStart('0').TrimEnd('0').Length > 28 || scale > 28 ||
                !Decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out decimal exact))
            {
                throw Invalid("Exact numeric literal exceeds the supported decimal precision or scale (28).");
            }
            return new LiteralValue(exact.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Decimal);
        }

        if (Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return new LiteralValue(number.ToString("R", CultureInfo.InvariantCulture), QueryScalarKind.FloatingPoint);
        }

        throw Invalid($"Unsupported GQL literal '{text}'.");
    }

    private static bool TryConjunctiveComparisons(GraphPatternFilter filter,
        out IReadOnlyList<GraphPatternPropertyComparison> comparisons)
    {
        var found = new List<GraphPatternPropertyComparison>();
        bool Visit(GraphPatternFilter current)
        {
            switch (current)
            {
                case GraphPatternPropertyComparison comparison:
                    found.Add(comparison);
                    return true;
                case GraphPatternBooleanFilter { Operator: GraphPatternBooleanOperator.And } boolean:
                    return Visit(boolean.Left) && Visit(boolean.Right);
                default:
                    return false;
            }
        }
        bool conjunctive = Visit(filter);
        comparisons = found;
        return conjunctive;
    }

    private sealed class PredicateExpressionParser
    {
        private readonly string[] tokens;
        private int index;

        public PredicateExpressionParser(string source)
        {
            tokens = Tokenize(source).ToArray();
            if (tokens.Length > 256)
            {
                throw Invalid("A GQL WHERE expression cannot exceed 256 tokens.");
            }
        }

        public int PredicateCount
        {
            get; private set;
        }

        public GraphPatternFilter Parse()
        {
            if (tokens.Length == 0)
            {
                throw Invalid("A WHERE expression is required.");
            }

            GraphPatternFilter result = ParseOr();
            if (index != tokens.Length)
            {
                throw Invalid($"Unexpected WHERE token '{tokens[index]}'.");
            }

            return result;
        }

        private GraphPatternFilter ParseOr()
        {
            GraphPatternFilter result = ParseXor();
            while (Take("OR"))
            {
                result = new GraphPatternBooleanFilter(result,
                GraphPatternBooleanOperator.Or, ParseXor());
            }

            return result;
        }

        private GraphPatternFilter ParseXor()
        {
            GraphPatternFilter result = ParseAnd();
            while (Take("XOR"))
            {
                result = new GraphPatternBooleanFilter(result, GraphPatternBooleanOperator.Xor, ParseAnd());
            }
            return result;
        }

        private GraphPatternFilter ParseAnd()
        {
            GraphPatternFilter result = ParseUnary();
            while (Take("AND"))
            {
                result = new GraphPatternBooleanFilter(result,
                GraphPatternBooleanOperator.And, ParseUnary());
            }

            return result;
        }

        private GraphPatternFilter ParseUnary()
        {
            if (Take("NOT"))
            {
                return new GraphPatternNotFilter(ParseUnary());
            }

            if (Take("("))
            {
                GraphPatternFilter nested = ParseOr();
                if (!Take(")"))
                {
                    throw Invalid("A parenthesized WHERE expression must be closed.");
                }

                return nested;
            }
            return ParseComparison();
        }

        private GraphPatternFilter ParseComparison()
        {
            string reference = Next("a bound property reference");
            Match matchedReference = PropertyReference.Match(reference);
            if (Take("IS"))
            {
                bool negated = Take("NOT");
                if (!Take("NULL") || reference.ToUpperInvariant() is "NULL" or "TRUE" or "FALSE" ||
                    !matchedReference.Success && !GraphGqlIdentifiers.Whole.IsMatch(reference))
                {
                    throw Invalid("IS [NOT] NULL requires a node or property reference.");
                }
                PredicateCount++;
                return new GraphPatternNullTest(matchedReference.Success ? matchedReference.Groups["binding"].Value : reference,
                    matchedReference.Success ? matchedReference.Groups["property"].Value : null, negated);
            }
            if (!matchedReference.Success)
            {
                throw Invalid($"WHERE term '{reference}' is not a bound property reference.");
            }

            string operation = Next("a comparison operator");
            if (operation is not ("=" or "<>" or "!=" or "<" or "<=" or ">" or ">="))
            {
                throw Invalid($"Unsupported comparison operator '{operation}'.");
            }

            string value = Next("a typed literal");
            PredicateCount++;
            QueryValue literal = Literal(value);
            if (RequiresScalarEquality(literal))
            {
                return new GraphPatternExpressionFilter(new(null, null, null, operation,
                    [new(matchedReference.Groups["binding"].Value, matchedReference.Groups["property"].Value, null), new(null, null, literal)]));
            }
            return new GraphPatternPropertyComparison(matchedReference.Groups["binding"].Value,
                matchedReference.Groups["property"].Value,
                literal, Comparison(operation));
        }

        private bool Take(string value)
        {
            if (index >= tokens.Length || !tokens[index].Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            index++;
            return true;
        }

        private string Next(string expected) => index < tokens.Length ? tokens[index++] :
            throw Invalid($"A WHERE expression requires {expected}.");

        private static IEnumerable<string> Tokenize(string source)
        {
            for (int position = 0; position < source.Length;)
            {
                if (Char.IsWhiteSpace(source[position]))
                {
                    position++;
                    continue;
                }
                char current = source[position];
                if (current is '(' or ')')
                {
                    yield return current.ToString();
                    position++;
                    continue;
                }
                if (current is '<' or '>' or '!' or '=')
                {
                    int length = position + 1 < source.Length && source[position + 1] is '=' or '>' ? 2 : 1;
                    yield return source.Substring(position, length);
                    position += length;
                    continue;
                }
                if (current == '"')
                {
                    int start = position++;
                    bool escaped = false;
                    while (position < source.Length)
                    {
                        char character = source[position++];
                        if (escaped)
                        {
                            escaped = false;
                            continue;
                        }
                        if (character == '\\')
                        {
                            escaped = true;
                            continue;
                        }
                        if (character == '"')
                        {
                            break;
                        }
                    }
                    if (source[position - 1] != '"')
                    {
                        throw Invalid("A quoted GQL literal is unterminated.");
                    }

                    yield return source[start..position];
                    continue;
                }
                int tokenStart = position;
                while (position < source.Length && !Char.IsWhiteSpace(source[position]) &&
                    source[position] is not ('(' or ')' or '<' or '>' or '!' or '='))
                {
                    position++;
                }

                if (position == tokenStart)
                {
                    throw Invalid($"Unsupported WHERE character '{source[position]}'.");
                }

                yield return source[tokenStart..position];
            }
        }
    }

    private static IEnumerable<string> SplitPatternClauses(string value)
    {
        int start = 0;
        int quantifierDepth = 0;
        string masked = MaskQuotedStrings(value);
        for (int index = 0; index < value.Length; index++)
        {
            if (masked[index] == '{')
            {
                quantifierDepth++;
            }
            else if (masked[index] == '}')
            {
                quantifierDepth--;
            }
            else if (masked[index] == ',' && quantifierDepth == 0)
            {
                string clause = value[start..index].Trim();
                if (clause.Length > 0)
                {
                    yield return clause;
                }
                else
                {
                    throw Invalid("A comma-separated MATCH component cannot be empty.");
                }

                start = index + 1;
            }
        }
        string final = value[start..].Trim();
        if (final.Length > 0)
        {
            yield return final;
        }
        else if (start > 0)
        {
            throw Invalid("A comma-separated MATCH component cannot be empty.");
        }
    }

    private static MutableNode Mutable(Match match, string binding) => new(binding,
        match.Groups["label"].Captures.Select(capture => GraphGqlIdentifiers.DecodeName(capture.Value)).Distinct(StringComparer.Ordinal).ToList(),
        InlinePredicates(match).ToList());

    private static IEnumerable<GraphPatternPropertyPredicate> InlinePredicates(Match match)
    {
        if (Value(match, "properties") is not { } properties || String.IsNullOrWhiteSpace(properties))
        {
            yield break;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string item in SplitProjectionItems(properties))
        {
            Match entry = GqlRegex.Match(item, @"^\s*(?<name>" + GraphGqlIdentifiers.NamePattern + @")\s*:\s*(?<value>.+)$");
            string name = entry.Success ? GraphGqlIdentifiers.DecodeName(entry.Groups["name"].Value) : String.Empty;
            if (!entry.Success || !names.Add(name))
            {
                throw Invalid("Inline properties require unique property names and value expressions.");
            }
            GraphScalarExpression value = ParseExpression(entry.Groups["value"].Value);
            QueryValue? literal = value.Literal;
            if (literal is null && IsConstantExpression(value))
            {
                literal = EntityForge.Db.Graph.GraphScalarEvaluator.Evaluate(value, (_, _) => UnboundValue.Instance);
            }
            if (literal is null)
            {
                throw Invalid("Inline property values require parameters or deterministic expressions without external variable references.");
            }
            yield return new(name, literal);
        }
    }

    private static bool IsConstantExpression(GraphScalarExpression expression) => expression.Literal is not null ||
        expression.Function is not null and not ("RAND" or "RANDOMUUID") &&
        (expression.Function != "UUID" || expression.Arguments!.Count != 0) &&
        !expression.Function.EndsWith(".REALTIME", StringComparison.Ordinal) && expression.Arguments!.All(IsConstantExpression);

    private static bool RequiresScalarEquality(QueryValue value) => value is ListValue or LiteralValue
    { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint or QueryScalarKind.Duration };

    private static GraphPatternFilter ScalarEquality(string binding, string property, QueryValue value) =>
        new GraphPatternExpressionFilter(new(null, null, null, "=", [new(binding, property, null), new(null, null, value)]));

    private static GraphPatternNode Freeze(MutableNode node) => new(node.Binding, node.Labels,
        node.Predicates.Where(p => !RequiresScalarEquality(p.Value)).ToArray());

    private static string? Value(Match match, string group) => match.Groups[group].Success
        ? group == "label" ? GraphGqlIdentifiers.DecodeName(match.Groups[group].Value) : match.Groups[group].Value : null;

    private static ArgumentException Invalid(string message, Exception? inner = null) =>
        new(message, nameof(GraphGqlQueryRequest.Query), inner);

    private sealed record MutableNode(string Binding, List<string> Labels,
        List<GraphPatternPropertyPredicate> Predicates);
}

internal static partial class GraphGqlPropertyProjectionShaper
{
    internal static QueryValue Evaluate(GraphPatternMatch match, GraphScalarExpression expression) => Operand(match, expression);

    internal static GraphPatternMatch Project(GqlPropertyProjection projection,
        Func<string, string, QueryValue> property) => new(projection.Terms.Select(term =>
            new KeyValuePair<string, QueryValue>(term.Alias, EntityForge.Db.Graph.GraphScalarEvaluator.Evaluate(term.Expression, property))), []);

    public static QueryResultEnvelope Apply(QueryResultEnvelope envelope, GqlPropertyProjection projection)
    {
        var source = (GraphPatternResult)envelope.Result;
        GraphPatternMatch[] matches = source.Matches.Select(match => new GraphPatternMatch(
            projection.Terms.Select(term => new KeyValuePair<string, QueryValue>(term.Alias,
                Project(match, term))), [])).ToArray();
        return new QueryResultEnvelope(envelope.QueryId, new GraphPatternResult(source.Graph, matches),
            envelope.CorrelationId, envelope.Evidence, envelope.SemanticModels, envelope.Diagnostics);
    }

    private static QueryValue Project(GraphPatternMatch match, GqlPropertyProjectionTerm term)
        => Operand(match, term.Expression);

    private static QueryValue Operand(GraphPatternMatch match, GraphScalarExpression operand)
    {
        if (operand.Function is not null)
        {
            return EntityForge.Db.Graph.GraphScalarEvaluator.Evaluate(operand, (binding, property) => Operand(match, new(binding, property, null)));
        }
        if (operand.Literal is not null)
        {
            return operand.Literal;
        }

        if (!match.Bindings.TryGetValue(operand.Binding!, out QueryValue? bound) || bound is UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (bound is MapValue map)
        {
            return map.Values.GetValueOrDefault(operand.Property!, UnboundValue.Instance);
        }
        if (bound is not (NodeValue or EdgeValue))
        {
            throw new ArgumentException("Property references currently require a record, node or edge.");
        }
        EntityForge.Db.Graph.GraphScalarEvaluator.RequireLiveEntity(bound);
        var properties = bound switch
        {
            NodeValue node => node.Properties,
            EdgeValue edge => edge.Properties,
            _ => throw new NotSupportedException("A GQL property expression requires a graph element binding.")
        };

        string identifier = $"urn:entityforge:graph:property:{Uri.EscapeDataString(operand.Property!)}";
        return properties.SingleOrDefault(property =>
            property.SourceTerm.Identifier.AbsoluteUri == identifier)?.Value ?? UnboundValue.Instance;
    }

}
