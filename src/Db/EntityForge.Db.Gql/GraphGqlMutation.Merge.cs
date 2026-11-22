using System.Globalization;
using System.Text.RegularExpressions;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlMutationCompiler
{
    private static CompiledGraphGqlMutation CompileMerge(string source, Guid? transactionId)
    {
        // A standalone MERGE is a one-row pipeline. Use the same captured-state
        // matcher and action binder as read-stage MERGE, including common SET
        // clauses after ON CREATE/ON MATCH. The private seed cannot be referenced
        // by authored expressions because its name is absent from their tokens.
        string seed = "__mergeInput";
        string masked = GraphGqlQueryCompiler.MaskQuotedStrings(source);
        while (GqlRegex.IsMatch(masked, @"\b" + seed + @"\b"))
        {
            seed += "_";
        }
        return CompileRowMerge("UNWIND [0] AS " + seed + " MERGE " + source, transactionId)
            ?? throw new ArgumentException("Invalid MERGE pattern.");
    }

    // Describe the proposed native creations for expression evaluation. These are
    // values only: no temporary database or intermediate commit is involved.
    private static QueryResultEnvelope CreationBindings(IReadOnlyList<GraphMutation> mutations, IReadOnlyDictionary<string, Guid> bindings)
    {
        static IdentifierValue Identity(Guid id) => new($"urn:uuid:{id:D}");
        static SemanticTerm Term(string kind, string name, SemanticTermRole role) =>
            new(new Uri($"urn:entityforge:graph:{kind}:{Uri.EscapeDataString(name)}"), role);
        static QueryValue Value(DatabaseValue value) => value switch
        {
            DatabaseValue.String text => new LiteralValue(text.Value),
            DatabaseValue.Int64 number => new LiteralValue(number.Value.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer),
            DatabaseValue.Double number => new LiteralValue(number.Value.ToString("R", CultureInfo.InvariantCulture), QueryScalarKind.FloatingPoint),
            DatabaseValue.Boolean boolean => new LiteralValue(boolean.Value ? "true" : "false", QueryScalarKind.Boolean),
            DatabaseValue.Date calendar => new LiteralValue(calendar.Value.ToString(), QueryScalarKind.Date),
            DatabaseValue.Duration duration => new LiteralValue(duration.Value.ToString(), QueryScalarKind.Duration, duration.DatatypeIdentifier),
            DatabaseValue.LocalDateTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.LocalDateTime),
            DatabaseValue.LocalTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.LocalTime),
            DatabaseValue.Decimal exact => new LiteralValue(exact.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Decimal),
            DatabaseValue.Uuid uuid => new LiteralValue(uuid.Value.ToString("D"), QueryScalarKind.Guid),
            DatabaseValue.ZonedTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.ZonedTime),
            DatabaseValue.ZonedDateTime time => new LiteralValue(time.Value.ToString(), QueryScalarKind.ZonedDateTime),
            DatabaseValue.Point point => new LiteralValue(point.Value.ToString(), QueryScalarKind.Point),
            DatabaseValue.DateTimeOffset date => new LiteralValue(date.Value.ToString("O", CultureInfo.InvariantCulture), QueryScalarKind.DateTime),
            DatabaseValue.Uri uri => new IdentifierValue(uri.Value.AbsoluteUri),
            DatabaseValue.Binary binary => new LiteralValue(Convert.ToBase64String(binary.Value.Span)),
            DatabaseValue.Json json => new JsonValue(json.Utf8Json),
            DatabaseValue.Array array => new ListValue(array.Value.Select(Value)),
            _ => throw new ArgumentException("A MERGE creation property requires a supported scalar.")
        };
        PropertyAssertion Property(string name, DatabaseValue value) => new(Term("property", name, SemanticTermRole.Property), Value(value));
        var nodes = mutations.OfType<CreateVertex>().ToDictionary(node => node.Vertex.Value, node => new NodeValue(Identity(node.Vertex.Value),
            properties: mutations.OfType<SetVertexProperty>().Where(property => property.Vertex == node.Vertex).Select(property => Property(property.Name, property.Value)),
            labels: node.Labels));
        var edges = mutations.OfType<CreateEdge>().ToDictionary(edge => edge.Edge.Value, edge => new EdgeValue(
            Identity(edge.From.Value), Identity(edge.To.Value), new RelationshipAssertion(Term("relationship", edge.Label, SemanticTermRole.Relationship)),
            Identity(edge.Edge.Value), mutations.OfType<SetEdgeProperty>().Where(property => property.Edge == edge.Edge).Select(property => Property(property.Name, property.Value))));
        var row = new GraphPatternMatch(bindings.Select(binding => new KeyValuePair<string, QueryValue>(binding.Key,
            nodes.TryGetValue(binding.Value, out var node) ? node : edges[binding.Value])), []);
        return new(Guid.Empty, new GraphPatternResult(new GraphResult(nodes.Values, edges.Values), [row]));
    }
}
