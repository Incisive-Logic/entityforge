using System.Globalization;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Results;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Gql;

// A statement-local view of proposed values, used only while preparing native
// mutations. Earlier input rows are visible to later rows without early commits.
internal sealed class GqlMutationValueView
{
    private readonly Dictionary<(bool Node, Guid Id), QueryValue> _entities = [];

    internal void Observe(GraphResult graph)
    {
        foreach (var node in graph.Nodes)
        {
            Observe(node);
        }
        foreach (var edge in graph.Edges)
        {
            Observe(edge);
        }
    }

    internal void Observe(GraphPatternMatch row)
    {
        foreach (var value in row.Bindings.Values)
        {
            Observe(value);
        }
    }

    private void Observe(QueryValue value)
    {
        GraphElementIdentity? identity = value switch
        {
            NodeValue node => node.Identity,
            EdgeValue edge => edge.Identity,
            _ => null
        };
        if (identity is IdentifierValue identifier && identifier.Identifier.StartsWith("urn:uuid:", StringComparison.Ordinal))
        {
            _entities.TryAdd((value is NodeValue, Guid.Parse(identifier.Identifier[9..])), value);
            return;
        }
        IEnumerable<QueryValue> nested = value switch
        {
            ListValue list => list.Items,
            MapValue map => map.Values.Values,
            PathValue path => path.Elements,
            _ => []
        };
        foreach (var item in nested)
        {
            Observe(item);
        }
    }

    internal GraphResult Refresh(GraphResult graph) => new(
        graph.Nodes.Select(node => (NodeValue)Refresh(node)),
        graph.Edges.Select(edge => (EdgeValue)Refresh(edge)));

    internal GraphPatternMatch Refresh(GraphPatternMatch match) => new(
        match.Bindings.Select(pair => new KeyValuePair<string, QueryValue>(pair.Key, Refresh(pair.Value))), match.Paths);

    internal QueryValue Refresh(QueryValue value)
    {
        GraphElementIdentity? identity = value switch
        {
            NodeValue node => node.Identity,
            EdgeValue edge => edge.Identity,
            _ => null
        };
        if (identity is IdentifierValue identifier && identifier.Identifier.StartsWith("urn:uuid:", StringComparison.Ordinal))
        {
            var key = (value is NodeValue, Guid.Parse(identifier.Identifier[9..]));
            if (_entities.TryGetValue(key, out var current))
            {
                return current;
            }
            _entities.Add(key, value);
        }
        return value switch
        {
            ListValue list => new ListValue(list.Items.Select(Refresh)),
            MapValue map => new MapValue(map.Values.Select(pair => new KeyValuePair<string, QueryValue>(pair.Key, Refresh(pair.Value)))),
            PathValue path => new PathValue(path.Elements.Select(Refresh)),
            _ => value
        };
    }

    internal void Apply(IEnumerable<GraphMutation> mutations)
    {
        foreach (var mutation in mutations)
        {
            (bool Node, Guid Id)? key = mutation switch
            {
                SetVertexProperty item => (true, item.Vertex.Value),
                RemoveVertexProperty item => (true, item.Vertex.Value),
                AddVertexLabel item => (true, item.Vertex.Value),
                RemoveVertexLabel item => (true, item.Vertex.Value),
                SetEdgeProperty item => (false, item.Edge.Value),
                RemoveEdgeProperty item => (false, item.Edge.Value),
                DeleteVertex item => (true, item.Vertex.Value),
                DeleteEdge item => (false, item.Edge.Value),
                _ => null
            };
            if (key is null || !_entities.TryGetValue(key.Value, out var value))
            {
                continue;
            }
            if (mutation is DeleteVertex or DeleteEdge)
            {
                _entities[key.Value] = EntityForge.Db.Graph.GraphScalarEvaluator.DeletedCopy(value);
                continue;
            }
            var properties = (value is NodeValue node ? node.Properties : ((EdgeValue)value).Properties).ToList();
            string? name = mutation switch
            {
                SetVertexProperty item => item.Name,
                RemoveVertexProperty item => item.Name,
                SetEdgeProperty item => item.Name,
                RemoveEdgeProperty item => item.Name,
                _ => null
            };
            if (name is not null)
            {
                var term = new SemanticTerm(new Uri("urn:entityforge:graph:property:" + Uri.EscapeDataString(name)), SemanticTermRole.Property);
                properties.RemoveAll(property => property.SourceTerm.Identifier == term.Identifier);
                DatabaseValue? assigned = mutation switch
                {
                    SetVertexProperty item => item.Value,
                    SetEdgeProperty item => item.Value,
                    _ => null
                };
                if (assigned is not null)
                {
                    properties.Add(new PropertyAssertion(term, Scalar(assigned)));
                }
            }
            if (value is NodeValue vertex)
            {
                var labels = vertex.Labels.ToHashSet(StringComparer.Ordinal);
                if (mutation is AddVertexLabel added)
                {
                    labels.Add(added.Label);
                }
                if (mutation is RemoveVertexLabel removed)
                {
                    labels.Remove(removed.Label);
                }
                _entities[key.Value] = new NodeValue(vertex.Identity, vertex.Types, properties, labels);
            }
            else
            {
                var edge = (EdgeValue)value;
                _entities[key.Value] = new EdgeValue(edge.Source, edge.Target, edge.Relationship, edge.Identity, properties);
            }
        }
    }

    private static QueryValue Scalar(DatabaseValue value) => value switch
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
        DatabaseValue.Array array => new ListValue(array.Value.Select(Scalar)),
        _ => throw new ArgumentException("A GQL assignment requires a supported scalar.")
    };
}
