using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Distributed.Sharding;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Semantics;

namespace EntityForge.Db.Distributed.Hosting;

// Exchanges are complete bounded relations at a protected cut. A limit is a failure,
// never an implicit LIMIT on the input to a join, aggregate, or negative predicate.
internal sealed record PartitionQueryRelation(GraphVertex[]? Vertices = null, GraphEdge[]? Edges = null,
    RdfQuad[]? Quads = null, SemanticFieldEvidence[]? Fields = null,
    SemanticRelationshipEvidence[]? Relationships = null, SemanticCatalogSnapshot? Catalog = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GraphEdgeReference[]? Adjacency = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    Guid[]? GraphBindings = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    RdfBindingFragment[]? RdfBindings = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    SemanticCompactRow[]? SemanticRows = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PortableEvidenceResponse[]? SemanticEvidence = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool ProfileFallback = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PartitionTopologyProperties.Bag[]? PropertyBags = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    GraphFragmentRow[]? ScalarRows = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PartitionQueryContinuation[]? Continuations = null);

internal sealed record PartitionQueryExchangeRequest(string Language, string? Statement = null,
    PortableEntityQueryRequest? EntityQuery = null, int Offset = 0,
    PartitionGraphQueryFrontier? GraphFrontier = null, bool CompleteQuery = false, bool GraphFragments = false,
    bool ProfileFragments = false, bool IncludeSupportingGraph = false, bool ContinueLocal = false,
    RdfBindingFrontier? RdfFrontier = null, PartitionSemanticFrontier? SemanticFrontier = null);

internal enum PartitionGraphRoleOperator
{
    Native, PropertyRoots, Topology, Properties
}

internal sealed record PartitionGraphQueryFrontier(int Stage, Guid[]? Vertices = null, Guid[]? Edges = null,
    PartitionGraphRoleOperator RoleOperator = PartitionGraphRoleOperator.Native);

internal sealed record PartitionQueryExchangePage(PartitionQueryRelation Relation, int? NextOffset,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    PartitionProjectionValue? Result = null);

internal sealed partial class PartitionOwnedNode
{
    private async ValueTask<PartitionProjectionValue> ReadQueryRelationAsync(DatabaseVersion version,
        PartitionReadQuery query, CancellationToken token)
    {
        var relation = await CaptureQueryRelationAsync(version, query, token);
        PartitionProjectionValue.Measure(relation, PartitionParticipantWire.MaximumResponseBytes, PartitionQueryJson.Options);
        return new(relation, PartitionQueryJson.Options);
    }

    internal async ValueTask<PartitionQueryRelation> CaptureQueryRelationAsync(DatabaseVersion version,
        PartitionReadQuery query, CancellationToken token)
    {
        if (query.Identity != Guid.Empty || query.Subject is not null || query.Label is not null ||
            query.Batch is not null || query.Source is not null)
        {
            throw new InvalidDataException("Invalid query exchange request.");
        }

        if (query.QueryExchange is { ProfileFragments: true } compact)
        {
            return await CaptureProfileFragmentsAsync(version, compact, token);
        }

        if (query.QueryExchange is { SemanticFrontier: not null } selective)
        {
            return await CaptureSemanticFrontierAsync(version, selective, token);
        }

        const int maximum = PartitionQueryLimits.MaximumFacts;
        PartitionQueryRelation relation;
        if (_native.Database is GraphPropertyShard split)
        {
            if (query.QueryExchange is { GraphFragments: true, GraphFrontier: { } roleFrontier, Statement: { } roleStatement })
            {
                return CaptureSplitFrontier(split, version, PartitionQueryCompilation.CompileGraph(roleStatement).Query, roleFrontier, token, query.QueryExchange.IncludeSupportingGraph ? null : PartitionQueryCompilation.CompileGraph(roleStatement).Projection);
            }

            if (query.QueryExchange is { GraphFragments: true } or { GraphFrontier: not null } or { CompleteQuery: true })
            {
                throw new NotSupportedException("Split Graph queries require complete role exchange.");
            }

            token.ThrowIfCancellationRequested();
            return CaptureSplitRelation(split, version);
        }
        if (_native.Database is GraphDatabaseEngine graph)
        {
            var view = graph.CaptureDistributedQueryView(version);
            var compiled = query.QueryExchange is { Language: "gql", Statement: { } statement }
                ? PartitionQueryCompilation.CompileGraph(statement) : null;
            var pattern = compiled is { RequiresCompleteGraph: false } ? compiled.Query : null;
            if (query.QueryExchange?.GraphFrontier is { } frontier)
            {
                return CaptureGraphQueryFrontier(view, pattern ?? throw new InvalidDataException("Missing frontier query."), frontier, token, query.QueryExchange.GraphFragments, query.QueryExchange.ContinueLocal, query.QueryExchange.IncludeSupportingGraph ? null : PartitionQueryCompilation.CompileGraph(query.QueryExchange.Statement!).Projection);
            }

            var vertices = (pattern is null ? view.Vertices.Values : BoundedGraphPatternExecutor.ExchangeVertices(view, pattern, token))
                .Take(maximum + 1).ToArray();
            var edges = view.Edges.Values.Where(e => (query.Kind == "serving-relation" || e.StorageRole.HasFlag(GraphEdgeStorageRole.Authoritative)) &&
                    (pattern is null || pattern.Relationships.Any(r => r.Label is null || r.Label == e.Label)))
                .Take(maximum + 1).ToArray();
            if (vertices.Length + edges.Length > maximum)
            {
                throw new InvalidOperationException("Graph query exchange exceeds its fact bound.");
            }

            if (pattern is not null && query.QueryExchange is { IncludeSupportingGraph: false, Statement: { } projectedStatement } &&
                PartitionQueryCompilation.CompileGraph(projectedStatement).Projection is { } projection)
            {
                var columns = GraphEvaluationColumns(pattern, projection);
                vertices = vertices.Select(vertex => vertex with { Properties = columns.Project(vertex.Properties) }).ToArray();
                edges = edges.Select(edge => edge with { Properties = columns.Project(edge.Properties) }).ToArray();
            }
            relation = new(Vertices: vertices, Edges: edges);
        }
        else if (_native.Database is IRdfDatabase rdf)
        {
            using var snapshot = _timestampProjection.Value && rdf is RdfDatabaseEngine timestampRdf
                ? timestampRdf.AcquireTimestampProjectionLease(version) : await rdf.AcquireSnapshotAsync(version, token);
            var patterns = query.QueryExchange is { Language: "sparql", Statement: { } statement }
                ? RdfSparql.ExchangePatterns(statement) : null;
            var quads = new HashSet<RdfQuad>();
            foreach (var pattern in patterns ?? [new RdfQuadPattern()])
            {
                foreach (var quad in ((IRdfBoundedSnapshotReader)rdf).FindBounded(pattern, snapshot, maximum, token))
                {
                    if (quads.Add(quad) && quads.Count > maximum)
                    {
                        throw new InvalidOperationException("RDF query exchange exceeds its fact bound.");
                    }
                }
            }

            relation = new(Quads: quads.ToArray());
        }
        else if (_native.Database is SemanticDatabaseEngine semantic)
        {
            using var snapshot = await semantic.AcquireDistributedStateAsync(version, token);
            var entity = query.QueryExchange?.EntityQuery;
            var exact = PartitionQuerySelection.IsDirectScalar(entity)
                ? new SemanticTerm(new Uri(entity!.RootConcept), SemanticTermRole.Concept) : null;
            var evidence = await snapshot.CaptureQueryRelationAsync(maximum, token, exact);
            relation = new(Fields: evidence.Fields, Relationships: evidence.Relationships, Catalog: evidence.Catalog);
        }
        else
        {
            throw new NotSupportedException("Unsupported query profile.");
        }

        token.ThrowIfCancellationRequested();
        return relation;
    }

    private async ValueTask<PartitionProjectionValue> ReadQueryRelationPageAsync(DatabaseVersion version,
        PartitionReadQuery query, CancellationToken token)
    {
        var exchange = query.QueryExchange ?? throw new InvalidDataException("Missing exchange selection.");
        if (exchange.ContinueLocal && (!exchange.GraphFragments || exchange.GraphFrontier?.RoleOperator != PartitionGraphRoleOperator.Native))
        {
            throw new InvalidDataException("Local continuation requires a native Graph fragment.");
        }

        if (exchange.GraphFragments && (exchange.GraphFrontier is null || exchange.CompleteQuery || exchange.Language != "gql"))
        {
            throw new InvalidDataException("Invalid fragment selection.");
        }

        if (exchange.ProfileFragments && (exchange.CompleteQuery || exchange.GraphFragments || exchange.GraphFrontier is not null ||
            !PartitionProfileFragments.Supports(exchange.Language, exchange.Statement, exchange.EntityQuery)))
        {
            throw new InvalidDataException("Invalid profile fragment selection.");
        }

        if (exchange.RdfFrontier is not null && (!exchange.ProfileFragments || exchange.Language != "sparql" ||
            !RdfSparql.SupportsBindingFrontier(exchange.Statement!)))
        {
            throw new InvalidDataException("Invalid RDF frontier selection.");
        }

        if (exchange.SemanticFrontier is not null && (exchange.CompleteQuery || exchange.ProfileFragments || exchange.GraphFragments ||
            exchange.GraphFrontier is not null || exchange.RdfFrontier is not null || exchange.Language != "entity" ||
            !PartitionSemanticFrontier.Supports(exchange.EntityQuery)))
        {
            throw new InvalidDataException("Invalid Semantic frontier selection.");
        }

        if (exchange.Offset is < 0 or > PartitionQueryLimits.MaximumFacts ||
            exchange.Statement?.Length > PartitionQueryLimits.MaximumStatementCharacters ||
            (Domain.Profile == DatabaseProfileId.PropertyGraph ? exchange.Language != "gql" :
             Domain.Profile == DatabaseProfileId.Rdf ? exchange.Language != "sparql" : exchange.Language != "entity"))
        {
            throw new InvalidDataException("Invalid exchange association.");
        }

        if (exchange.CompleteQuery && exchange.Offset == 0 &&
            await TryExecuteOwnerQueryAsync(version, exchange, token) is { } completed)
        {
            return completed;
        }

        PartitionQueryRelation relation;
        var workKey = new PartitionQueryWorkKey(exchange.GraphFrontier, exchange.RdfFrontier, exchange.SemanticFrontier);
        var workspace = _queryWorkspace.Value;
        object? operatorView = _operatorView.Value;
        var operatorKey = operatorView is not null && workspace?.OperatorIdentity is { } interpretation
            ? new PartitionOperatorKey(interpretation, workKey) : null;
        PartitionCachedOperator? cached = null;
        long? retainedBytes = null;
        if (workspace is null || !workspace.TryGet(workKey, out relation!))
        {
            using var capture = DistributedReadTelemetry.Start("partition.query.relation.capture");
            if (operatorKey is not null && _operatorCache.TryGet(operatorView!, operatorKey, out cached))
            {
                capture?.SetTag("entityforge.operator_cache_hit", true);
                token.ThrowIfCancellationRequested();
                relation = cached.Relation;
                retainedBytes = workspace!.Add(workKey, relation, cached.RelationBytes);
            }
            else
            {
                capture?.SetTag("entityforge.operator_cache_hit", false);
                relation = await CaptureQueryRelationAsync(version, query, token);
                // Stable order under the same protected cut makes page replay exact. Pages
                // do not create an independently owned cache or outlive that cut.
                relation = relation with
                {
                    Vertices = relation.Vertices?.OrderBy(v => v.Id.Value).ToArray(),
                    Edges = relation.Edges?.OrderBy(e => e.Id.Value).ToArray(),
                    Quads = relation.Quads?.OrderBy(q => JsonSerializer.Serialize(q, PartitionParticipantWire.Json), StringComparer.Ordinal).ToArray(),
                    Fields = relation.Fields?.OrderBy(f => f.AssertionId).ToArray(),
                    Relationships = relation.Relationships?.OrderBy(r => r.AssertionId).ToArray(),
                    Adjacency = relation.Adjacency?.OrderBy(e => e.Id.Value).ToArray(),
                    GraphBindings = relation.GraphBindings?.Order().ToArray(),
                    ScalarRows = relation.ScalarRows?.OrderBy(v => v.Id.Value).ToArray(),
                    PropertyBags = relation.PropertyBags?.OrderBy(b => b.Edge).ThenBy(b => b.Identity).ToArray()
                };
                retainedBytes = workspace?.Add(workKey, relation);
            }
        }
        int total = (relation.Vertices?.Length ?? 0) + (relation.Edges?.Length ?? 0) + (relation.Quads?.Length ?? 0) +
            (relation.Fields?.Length ?? 0) + (relation.Relationships?.Length ?? 0) + (relation.Adjacency?.Length ?? 0) + (relation.GraphBindings?.Length ?? 0) +
            (relation.RdfBindings?.Length ?? 0) + (relation.SemanticRows?.Length ?? 0) + (relation.SemanticEvidence?.Length ?? 0) + (relation.PropertyBags?.Length ?? 0) + (relation.ScalarRows?.Length ?? 0);
        if (exchange.Offset > total)
        {
            throw new InvalidDataException("Exchange offset exceeds the relation.");
        }

        int count = Math.Min(PartitionQueryLimits.ExchangePageFacts, total - exchange.Offset);
        if (cached is not null && exchange.Offset == 0 && count == total)
        {
            token.ThrowIfCancellationRequested();
            return cached.Page;
        }

        while (true)
        {
            int skip = exchange.Offset, remaining = count;
            T[]? Slice<T>(T[]? values)
            {
                if (values is null)
                {
                    return null;
                }

                int start = Math.Min(skip, values.Length);
                skip -= start;
                int take = Math.Min(remaining, values.Length - start);
                remaining -= take;
                return values.AsSpan(start, take).ToArray();
            }
            var page = new PartitionQueryExchangePage(exchange.Offset == 0 && count == total ? relation : new(Slice(relation.Vertices), Slice(relation.Edges), Slice(relation.Quads),
                Slice(relation.Fields), Slice(relation.Relationships), exchange.Offset == 0 ? relation.Catalog : null, Slice(relation.Adjacency), Slice(relation.GraphBindings),
                Slice(relation.RdfBindings), Slice(relation.SemanticRows), Slice(relation.SemanticEvidence), relation.ProfileFallback, Slice(relation.PropertyBags), Slice(relation.ScalarRows), exchange.Offset == 0 ? relation.Continuations : null),
                exchange.Offset + count < total ? exchange.Offset + count : null);
            try
            {
                long pageBytes = PartitionProjectionValue.Measure(page, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options);
                PartitionProjectionValue? preparedPage = null;
                if (operatorKey is not null && cached is null && retainedBytes is { } size &&
                    exchange.Offset == 0 && count == total && !relation.ProfileFallback)
                {
                    token.ThrowIfCancellationRequested();
                    var wire = PartitionQueryOperatorWire.EncodePage(page);
                    var ownedPage = new PartitionProjectionValue(page, PartitionQueryJson.Options) { QueryPageBytes = pageBytes, OperatorWirePage = wire };
                    preparedPage = ownedPage;
                    _operatorCache.Add(operatorView!, operatorKey, new(relation, size, ownedPage), checked(size + operatorKey.InterpretationBytes + wire.CalculateSize()));
                }
                return preparedPage ?? new(page, PartitionQueryJson.Options)
                {
                    QueryPageBytes = pageBytes
                };
            }
            catch (InvalidOperationException) when (count > 1)
            {
                count /= 2;
            }
        }
    }
}

/// <summary>Fail-closed work limits for the bounded distributed relation evaluator.</summary>
public static class PartitionQueryLimits
{
    /// <summary>Gets the maximum number of facts admitted to a distributed query.</summary>
    public const int MaximumFacts = 16_384;
    /// <summary>Gets the maximum cumulative bytes exchanged by a query.</summary>
    public const int MaximumExchangeBytes = 8 * 1024 * 1024;
    /// <summary>Gets the maximum length of a submitted native query statement, in characters.</summary>
    public const int MaximumStatementCharacters = 16_384;
    /// <summary>Gets the maximum facts requested in one exchange page.</summary>
    public const int ExchangePageFacts = 256;
    /// <summary>Gets the maximum bytes requested in one exchange page.</summary>
    public const int ExchangePageBytes = 256 * 1024;
    /// <summary>Gets the maximum encoded bytes emitted by a streamed query result.</summary>
    public const int MaximumStreamBytes = 16 * 1024 * 1024;
}

internal static class PartitionQuerySelection
{
    internal static bool IsDirectScalar(PortableEntityQueryRequest? entity) => entity is not null && String.Equals(entity.ConceptMatchMode, "direct", StringComparison.OrdinalIgnoreCase) &&
        entity.Projection.Relationships is not { Count: > 0 } && !HasRelationship(entity.Filter);

    private static bool HasRelationship(PortableEntityFilterRequest? filter) => filter is not null &&
        (filter.Relationship is not null || filter.TargetConcept is not null || filter.TargetFilter is not null ||
         HasRelationship(filter.Operand) || filter.Operands?.Any(HasRelationship) == true);
}

internal static class PartitionQueryJson
{
    internal static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(PartitionParticipantWire.Json);
        options.Converters.Add(new PartitionQueryContinuationConverter());
        options.Converters.Add(new ProvenanceConverter());
        options.Converters.Add(new VocabularyConverter());
        return options;
    }

    private sealed class VocabularyConverter : System.Text.Json.Serialization.JsonConverter<EntityForge.Semantics.SemanticVocabulary>
    {
        public override EntityForge.Semantics.SemanticVocabulary Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            SemanticVocabularySerialization.Deserialize(reader.GetBytesFromBase64());

        public override void Write(Utf8JsonWriter writer, EntityForge.Semantics.SemanticVocabulary value, JsonSerializerOptions options) =>
            writer.WriteBase64StringValue(SemanticVocabularySerialization.Serialize(value));
    }

    private sealed class ProvenanceConverter : System.Text.Json.Serialization.JsonConverter<EntityForge.Semantics.SemanticProvenance>
    {
        public override EntityForge.Semantics.SemanticProvenance Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return SemanticEntityReadDecoder.Provenance(document.RootElement);
        }

        public override void Write(Utf8JsonWriter writer, EntityForge.Semantics.SemanticProvenance value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, PartitionParticipantWire.Json);
    }
}
