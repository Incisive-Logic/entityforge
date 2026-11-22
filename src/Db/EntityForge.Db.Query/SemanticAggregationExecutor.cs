using System.Diagnostics;
using System.Globalization;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Semantic;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Query;

internal sealed class SemanticAggregationExecutor
{
    private readonly PortableEntityQueryExecutor _entities = new();

    public async ValueTask<SemanticAggregationResult> ExecuteAsync(SemanticDatabaseState state,
        SemanticEntityAccessPaths accessPaths, SemanticAggregationQuery query, DatabaseVersion snapshot,
        CancellationToken cancellationToken)
    {
        Validate(query);
        EntityQueryDefinition source = Source(query);
        EntityProjectionProjectionResult projected = await _entities.ExecuteAsync(state, accessPaths, source,
            Options(), snapshot, cancellationToken).ConfigureAwait(false);
        if (projected.Document.Roots.Count > query.MaximumInputEntities)
        {
            throw new SemanticAggregationLimitException(
                $"Semantic aggregation exceeded {query.MaximumInputEntities} input entities.");
        }

        SemanticAggregationRow[] rows = Aggregate(projected.Document, query).ToArray();
        if (rows.Length > SemanticAggregationLimits.MaximumGroups)
        {
            throw new SemanticAggregationLimitException(
                $"Semantic aggregation exceeded {SemanticAggregationLimits.MaximumGroups} groups.");
        }

        HashSet<EvidenceReference> used = rows.SelectMany(row => row.Evidence).ToHashSet();
        EvidenceRecord[] evidence = projected.Document.Evidence.Where(item => used.Contains(item.Reference)).ToArray();
        return new(projected.Document.SourceQueryId, snapshot, rows, evidence, projected.Document.Roots.Count);
    }

    public async ValueTask<SemanticAggregationExplanation> ExplainAsync(SemanticDatabaseState state,
        SemanticEntityAccessPaths accessPaths, SemanticAggregationQuery query, bool analyze,
        DatabaseVersion snapshot, CancellationToken cancellationToken)
    {
        Validate(query);
        EntityQueryDefinition source = Source(query);
        SemanticEntityQueryExplanation plan = await _entities.ExplainAsync(state, accessPaths, source,
            Options(), analyze: false, snapshot, cancellationToken).ConfigureAwait(false);
        if (!analyze)
        {
            return new(snapshot, "semantic-group-aggregate", plan, query.MaximumInputEntities,
                null, null, null);
        }

        long started = Stopwatch.GetTimestamp();
        SemanticAggregationResult result = await ExecuteAsync(state, accessPaths, query, snapshot,
            cancellationToken).ConfigureAwait(false);
        return new(snapshot, "semantic-group-aggregate", plan, query.MaximumInputEntities,
            result.InputEntityCount, result.Rows.Count, Stopwatch.GetElapsedTime(started));
    }

    private static EntityQueryDefinition Source(SemanticAggregationQuery query)
    {
        SemanticTerm[] properties = query.Dimensions.Select(item => item.Property)
            .Concat(query.Measures.Where(item => item.Property is not null).Select(item => item.Property!))
            .Distinct().ToArray();
        EntityFieldProjection[] fields = properties.Select((property, index) =>
            new EntityFieldProjection(property, Alias(index))).ToArray();
        var projection = new EntityProjectionDefinition(query.Source.RootConcept, fields);
        return new(query.Source.RootConcept, projection, query.Source.Filter,
            page: new EntityQueryPage(checked(query.MaximumInputEntities + 1)),
            scopes: query.Source.Scopes, identities: query.Source.Identities,
            assertionPolicy: query.Source.AssertionPolicy);
    }

    private static EntityProjectionProjectionOptions Options() => new(
        MaximumRelationshipDepth: 0,
        UnmappedPropertyPolicy: UnmappedPropertyPolicy.IncludeBySourceIdentifier,
        IncludeEvidence: true);

    private static IEnumerable<SemanticAggregationRow> Aggregate(EntityProjection document,
        SemanticAggregationQuery query)
    {
        SemanticTerm[] properties = query.Dimensions.Select(item => item.Property)
            .Concat(query.Measures.Where(item => item.Property is not null).Select(item => item.Property!))
            .Distinct().ToArray();
        Dictionary<SemanticTerm, string> aliases = properties.Select((property, index) => (property, index))
            .ToDictionary(item => item.property, item => Alias(item.index));
        var groups = new Dictionary<GroupKey, List<EntityProjectionNode>>();
        foreach (EntityProjectionNode node in document.Roots)
        {
            IReadOnlyList<GroupKey> keys = GroupKeys(node, query.Dimensions, aliases);
            foreach (GroupKey key in keys)
            {
                if (!groups.TryGetValue(key, out List<EntityProjectionNode>? members))
                {
                    groups[key] = members = [];
                }

                members.Add(node);
            }
        }
        if (query.Dimensions.Count == 0 && groups.Count == 0)
        {
            groups[new([])] = [];
        }

        foreach ((GroupKey key, List<EntityProjectionNode> members) in groups
                     .OrderBy(item => item.Key.SortKey, StringComparer.Ordinal))
        {
            var values = new Dictionary<string, QueryValue>(StringComparer.Ordinal);
            var evidence = new HashSet<EvidenceReference>();
            for (int index = 0; index < query.Dimensions.Count; index++)
            {
                values[query.Dimensions[index].Name] = key.Values[index];
                foreach (EntityProjectionValue item in FieldValues(members, aliases[query.Dimensions[index].Property])
                             .Where(item => item.Value == key.Values[index]))
                {
                    evidence.UnionWith(item.Evidence);
                }
            }
            foreach (SemanticAggregationMeasure measure in query.Measures)
            {
                EntityProjectionValue[] measureValues = measure.Property is null ? [] :
                    FieldValues(members, aliases[measure.Property]).ToArray();
                values[measure.Name] = Measure(measure, members.Count, measureValues);
                evidence.UnionWith(measure.Function is SemanticAggregationFunction.CountEntities
                    ? members.SelectMany(member => member.Fields).SelectMany(field => field.Values)
                        .SelectMany(item => item.Evidence)
                    : measureValues.SelectMany(item => item.Evidence));
            }
            yield return new(values, evidence.OrderBy(item => item.Identifier.AbsoluteUri,
                StringComparer.Ordinal).ToArray());
        }
    }

    private static IReadOnlyList<GroupKey> GroupKeys(EntityProjectionNode node,
        IReadOnlyList<SemanticAggregationDimension> dimensions, IReadOnlyDictionary<SemanticTerm, string> aliases)
    {
        if (dimensions.Count == 0)
        {
            return [new([])];
        }

        IReadOnlyList<QueryValue[]> combinations = [[]];
        foreach (SemanticAggregationDimension dimension in dimensions)
        {
            QueryValue[] values = node.Fields.SingleOrDefault(item => item.Name == aliases[dimension.Property])?
                .Values.Select(item => item.Value).Distinct().ToArray() ?? [NullValue.Instance];
            combinations = combinations.SelectMany(prefix => values.Select(value => prefix.Append(value).ToArray()))
                .ToArray();
            if (combinations.Count > SemanticAggregationLimits.MaximumGroups)
            {
                throw new SemanticAggregationLimitException("One entity expanded into too many aggregate groups.");
            }
        }
        return combinations.Select(values => new GroupKey(values)).ToArray();
    }

    private static IEnumerable<EntityProjectionValue> FieldValues(IEnumerable<EntityProjectionNode> nodes, string alias) =>
        nodes.SelectMany(node => node.Fields.Where(field => field.Name == alias).SelectMany(field => field.Values));

    private static QueryValue Measure(SemanticAggregationMeasure measure, int entityCount,
        IReadOnlyList<EntityProjectionValue> values) => measure.Function switch
        {
            SemanticAggregationFunction.CountEntities => Integer(entityCount),
            SemanticAggregationFunction.CountValues => Integer(values.Count),
            SemanticAggregationFunction.CountDistinctValues => Integer(values.Select(item => item.Value).Distinct().Count()),
            SemanticAggregationFunction.Sum => DecimalValue(Numbers(values).Sum()),
            SemanticAggregationFunction.Average => values.Count == 0 ? NullValue.Instance :
                DecimalValue(Numbers(values).Average()),
            SemanticAggregationFunction.Minimum => values.Count == 0 ? NullValue.Instance :
                values.Select(item => item.Value).Aggregate((left, right) => Compare(left, right) <= 0 ? left : right),
            SemanticAggregationFunction.Maximum => values.Count == 0 ? NullValue.Instance :
                values.Select(item => item.Value).Aggregate((left, right) => Compare(left, right) >= 0 ? left : right),
            _ => throw new NotSupportedException($"Semantic aggregate '{measure.Function}' is unsupported.")
        };

    private static IEnumerable<decimal> Numbers(IEnumerable<EntityProjectionValue> values) => values.Select(item =>
        item.Value is LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal } literal
            ? Decimal.Parse(literal.LexicalValue, CultureInfo.InvariantCulture)
            : throw new ArgumentException("Sum and average require integer or decimal Semantic values."));

    private static LiteralValue Integer(long value) => new(value.ToString(CultureInfo.InvariantCulture),
        QueryScalarKind.Integer);

    private static LiteralValue DecimalValue(decimal value) => new(value.ToString(CultureInfo.InvariantCulture),
        QueryScalarKind.Decimal);

    private static string Alias(int index) => $"__aggregate_{index}";

    private static string Sort(QueryValue value) => value switch
    {
        LiteralValue literal => $"{literal.ScalarKind}:{literal.LexicalValue}",
        IdentifierValue identifier => $"identifier:{identifier.Identifier}",
        NullValue => "null:",
        _ => value.ToString() ?? String.Empty
    };

    private static int Compare(QueryValue left, QueryValue right) => (left, right) switch
    {
        (LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal } a,
            LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal } b) =>
            Decimal.Parse(a.LexicalValue, CultureInfo.InvariantCulture)
                .CompareTo(Decimal.Parse(b.LexicalValue, CultureInfo.InvariantCulture)),
        (LiteralValue a, LiteralValue b) when a.ScalarKind == b.ScalarKind =>
            StringComparer.Ordinal.Compare(a.LexicalValue, b.LexicalValue),
        (IdentifierValue a, IdentifierValue b) => StringComparer.Ordinal.Compare(a.Identifier, b.Identifier),
        _ => StringComparer.Ordinal.Compare(Sort(left), Sort(right))
    };

    private static void Validate(SemanticAggregationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Source);
        if (query.Source.Page is not null)
        {
            throw new ArgumentException("Semantic aggregation owns input bounding; the source cannot be paged.",
                nameof(query));
        }

        if (query.MaximumInputEntities <= 0 ||
            query.MaximumInputEntities > SemanticAggregationLimits.MaximumInputEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(query),
                $"Maximum input entities must be between 1 and {SemanticAggregationLimits.MaximumInputEntities}.");
        }

        if (query.Measures.Count == 0)
        {
            throw new ArgumentException("At least one aggregate measure is required.", nameof(query));
        }

        string[] names = query.Dimensions.Select(item => item.Name).Concat(query.Measures.Select(item => item.Name)).ToArray();
        if (names.Any(String.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new ArgumentException("Aggregate dimension and measure names must be non-empty and unique.", nameof(query));
        }

        if (query.Dimensions.Any(item => item.Property.Role is not SemanticTermRole.Property) ||
            query.Measures.Any(item => item.Property?.Role is not (null or SemanticTermRole.Property)))
        {
            throw new ArgumentException("Semantic aggregate dimensions and measures require property terms.", nameof(query));
        }

        if (query.Measures.Any(item => item.Function is SemanticAggregationFunction.CountEntities
                ? item.Property is not null : item.Property is null))
        {
            throw new ArgumentException("CountEntities has no property; every other measure requires one.", nameof(query));
        }
    }

    private sealed class GroupKey(IReadOnlyList<QueryValue> values) : IEquatable<GroupKey>
    {
        public IReadOnlyList<QueryValue> Values { get; } = values;

        public string SortKey { get; } = String.Join("\u001f", values.Select(Sort));

        public bool Equals(GroupKey? other) => other is not null && Values.SequenceEqual(other.Values);

        public override bool Equals(object? obj) => obj is GroupKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (QueryValue value in Values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
