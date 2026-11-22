using EntityForge.Db.Gql;
using System.Globalization;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Query.Adapters;

internal sealed record GraphPatternQueryResponse(
    Guid QueryId, long SnapshotVersion, IReadOnlyList<GraphPatternNodeResponse> Nodes,
    IReadOnlyList<GraphPatternEdgeResponse> Edges, IReadOnlyList<GraphPatternMatchResponse> Matches,
    IReadOnlyList<string> Evidence)
{
    public IReadOnlyList<QueryResultDiagnostic> Diagnostics { get; init; } = [];
}

internal sealed record GraphPatternNodeResponse(string Identity, IReadOnlyList<string> Labels,
    IReadOnlyDictionary<string, object?> Properties);

internal sealed record GraphPatternEdgeResponse(string Identity, string Source, string Target, string Relationship,
    IReadOnlyDictionary<string, object?> Properties);

internal sealed record GraphPatternMatchResponse(IReadOnlyDictionary<string, object?> Bindings,
    IReadOnlyList<IReadOnlyList<string>> Paths);

internal sealed record GraphProjectedQueryResponse(Guid QueryId, IReadOnlyList<GraphPatternMatchResponse> Matches)
{
    public IReadOnlyList<QueryResultDiagnostic> Diagnostics { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? SnapshotVersion
    {
        get; init;
    }
}

internal static class GraphPatternQueryResponseMapper
{
    internal static GraphProjectedQueryResponse Project(QueryResultEnvelope envelope) => (envelope.Result switch
    {
        GraphPatternResult graph => new GraphProjectedQueryResponse(envelope.QueryId, graph.Matches.Select(Match).ToArray()),
        RecordResult records => new(envelope.QueryId, records.Rows.Select(row => new GraphPatternMatchResponse(
            row.Values.ToDictionary(item => item.Key, item => Binding(item.Value), StringComparer.Ordinal), [])).ToArray()),
        _ => throw new NotSupportedException("Unsupported projected Graph result.")
    }) with
    {
        Diagnostics = GraphGqlCompletion.PublicDiagnostics(envelope)
    };

    public static GraphPatternQueryResponse Map(QueryResultEnvelope envelope, long snapshot)
    {
        return (envelope.Result switch
        {
            GraphPatternResult result => new GraphPatternQueryResponse(envelope.QueryId, snapshot,
                result.Graph.Nodes.Select(Node).ToArray(), result.Graph.Edges.Select(Edge).ToArray(),
                result.Matches.Select(Match).ToArray(), Evidence(envelope)),
            RecordResult result => MapRecords(envelope, snapshot, result),
            _ => throw new NotSupportedException(
                $"A graph response cannot map '{envelope.Result.GetType().Name}'.")
        }) with
        {
            Diagnostics = GraphGqlCompletion.PublicDiagnostics(envelope)
        };
    }

    private static GraphPatternQueryResponse MapRecords(QueryResultEnvelope envelope, long snapshot,
        RecordResult result)
    {
        NodeValue[] nodes = result.Rows.SelectMany(row => row.Values.Values).OfType<NodeValue>()
            .DistinctBy(node => node.Identity).ToArray();
        EdgeValue[] edges = result.Rows.SelectMany(row => row.Values.Values).OfType<EdgeValue>()
            .DistinctBy(edge => edge.Identity).ToArray();
        GraphPatternMatchResponse[] matches = result.Rows.Select(row => new GraphPatternMatchResponse(
            row.Values.ToDictionary(item => item.Key, item => Binding(item.Value), StringComparer.Ordinal), [])).ToArray();
        return new(envelope.QueryId, snapshot, nodes.Select(Node).ToArray(), edges.Select(Edge).ToArray(),
            matches, Evidence(envelope));
    }

    private static string[] Evidence(QueryResultEnvelope envelope) => envelope.Evidence
        .Select(item => item.Reference.Identifier.AbsoluteUri).ToArray();

    private static GraphPatternNodeResponse Node(NodeValue node) => new(Identity(node.Identity), node.Labels,
        node.Properties.ToDictionary(item => item.SourceTerm.Identifier.AbsoluteUri,
            item => Value(item.Value), StringComparer.Ordinal));

    private static GraphPatternEdgeResponse Edge(EdgeValue edge) => new(Identity(edge.Identity!),
        Identity(edge.Source), Identity(edge.Target), edge.Relationship.SourceTerm.Identifier.AbsoluteUri,
        edge.Properties.ToDictionary(item => item.SourceTerm.Identifier.AbsoluteUri,
            item => Value(item.Value), StringComparer.Ordinal));

    internal static GraphPatternMatchResponse Match(GraphPatternMatch match) => new(
        match.Bindings.ToDictionary(item => item.Key, item => Binding(item.Value), StringComparer.Ordinal),
        match.Paths.Select(path => (IReadOnlyList<string>)path.Elements.Select(Identity).ToArray()).ToArray());

    private static object? Binding(QueryValue value) => value switch
    {
        UnboundValue => null,
        PathValue path => path.Elements.Select(Identity).ToArray(),
        LiteralValue or JsonValue or NullValue or ListValue or MapValue => Value(value),
        _ => Identity(value)
    };

    private static string Identity(QueryValue value) => value switch
    {
        NodeValue node => Identity(node.Identity),
        EdgeValue edge => Identity(edge.Identity!),
        IdentifierValue id => id.Identifier,
        BlankNodeValue blank => blank.Identifier,
        _ => throw new NotSupportedException($"A graph binding cannot identify '{value.GetType().Name}'.")
    };

    private static object? Value(QueryValue value) => value switch
    {
        LiteralValue { ScalarKind: QueryScalarKind.Boolean } item => Boolean.Parse(item.LexicalValue),
        LiteralValue { ScalarKind: QueryScalarKind.Integer } item => Int64.Parse(item.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue { ScalarKind: QueryScalarKind.FloatingPoint } item => Double.TryParse(item.LexicalValue,
            NumberStyles.Float, CultureInfo.InvariantCulture, out double floating) && Double.IsFinite(floating) ? (object)floating : item.LexicalValue,
        LiteralValue { ScalarKind: QueryScalarKind.Decimal } item => Decimal.TryParse(item.LexicalValue,
            NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
            ? (object)number : Double.Parse(item.LexicalValue, CultureInfo.InvariantCulture),
        LiteralValue item => item.LexicalValue,
        IdentifierValue item => item.Identifier,
        JsonValue item => item.Value.ToJsonElement(),
        NullValue => null,
        ListValue items => items.Items.Select(Binding).ToArray(),
        MapValue map => map.Values.ToDictionary(pair => pair.Key, pair => Binding(pair.Value), StringComparer.Ordinal),
        _ => value.ToString()
    };
}
