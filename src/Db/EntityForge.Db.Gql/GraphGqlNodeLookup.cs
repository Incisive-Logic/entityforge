using EntityForge.Db.Graph;
using EntityForge.Query.Results;
using EntityForge.Query.Values;

namespace EntityForge.Db.Gql;

// Endpoint functions return the complete node from the same captured graph as
// the relationship, including its labels and properties. Mutation views keep
// these values current as earlier assignments are prepared atomically.
internal sealed class GqlNodeLookup
{
    private readonly IReadOnlyDictionary<GraphElementIdentity, NodeValue> _nodes;
    private readonly GqlMutationValueView? _view;

    internal GqlNodeLookup(GraphResult graph, GqlMutationValueView? view = null)
        : this(graph.Nodes.GroupBy(node => node.Identity).ToDictionary(group => group.Key, group => group.Last()), view)
    {
    }

    internal GqlNodeLookup(IReadOnlyDictionary<GraphElementIdentity, NodeValue> nodes, GqlMutationValueView? view = null)
    {
        _nodes = nodes;
        _view = view;
        if (view is not null)
        {
            foreach (var node in nodes.Values)
            {
                view.Refresh(node);
            }
        }
    }

    internal GraphScalarEvaluator.IterationBudget Budget(Action<long>? chargePathWork = null,
        CancellationToken cancellation = default) => new()
        {
            ChargePathWork = chargePathWork,
            Cancellation = cancellation,
            ResolveNode = identity => _nodes.TryGetValue(identity, out var node)
                ? _view?.Refresh(node) ?? node
                : throw new ArgumentException("The relationship endpoint is unavailable in the captured graph.")
        };

    internal static GraphResult IncludeBindings(GraphResult graph, IEnumerable<GraphPatternMatch> rows)
    {
        var nodes = graph.Nodes.GroupBy(node => node.Identity).ToDictionary(group => group.Key, group => group.Last());
        var edges = graph.Edges.GroupBy(edge => edge.Identity!).ToDictionary(group => group.Key, group => group.Last());
        var pending = new Stack<QueryValue>(rows.SelectMany(row => row.Bindings.Values));
        while (pending.TryPop(out var value))
        {
            switch (value)
            {
                case NodeValue node:
                    nodes[node.Identity] = node;
                    break;
                case EdgeValue edge:
                    edges[edge.Identity!] = edge;
                    break;
                case ListValue list:
                    foreach (var item in list.Items)
                    {
                        pending.Push(item);
                    }
                    break;
                case MapValue map:
                    foreach (var item in map.Values.Values)
                    {
                        pending.Push(item);
                    }
                    break;
                case PathValue path:
                    foreach (var item in path.Elements)
                    {
                        pending.Push(item);
                    }
                    break;
            }
        }
        return new(nodes.Values, edges.Values);
    }
}
