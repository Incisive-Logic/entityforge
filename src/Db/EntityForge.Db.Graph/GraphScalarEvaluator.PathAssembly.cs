using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    // Compiler building block for named variable-length subpaths. The ordered
    // edge bag comes from the original traversal, never from a second MATCH.
    // Endpoint values must come from that traversal's captured graph view.
    internal static QueryValue AssemblePatternPath(QueryValue start, QueryValue relationships, IterationBudget budget)
    {
        budget.Cancellation.ThrowIfCancellationRequested();
        RequireLiveEntity(start);
        if (start is NullValue or UnboundValue || relationships is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (start is not NodeValue current || relationships is not ListValue edges)
        {
            throw new ArgumentException("Path assembly requires a starting node and an ordered relationship list.");
        }
        if (edges.Items.Count > 4_999)
        {
            throw new ArgumentException("An assembled path exceeds 10,000 elements.");
        }
        var elements = new List<QueryValue>(1 + 2 * edges.Items.Count) { current };
        foreach (var value in edges.Items)
        {
            budget.Cancellation.ThrowIfCancellationRequested();
            budget.Consume();
            budget.ChargePathWork?.Invoke(1);
            RequireLiveEntity(value);
            if (value is not EdgeValue edge)
            {
                throw new ArgumentException("Path assembly requires relationship values.");
            }
            GraphElementIdentity next = edge.Source == current.Identity ? edge.Target :
                edge.Target == current.Identity ? edge.Source :
                throw new ArgumentException("An assembled path relationship must connect to the preceding node.");
            var resolved = next == current.Identity ? current : budget.ResolveNode?.Invoke(next);
            RequireLiveEntity(resolved ?? UnboundValue.Instance);
            if (resolved is not NodeValue node || node.Identity != next)
            {
                throw new ArgumentException("The path endpoint is unavailable in the captured graph.");
            }
            elements.Add(edge);
            elements.Add(node);
            current = node;
        }
        return new PathValue(elements);
    }
}
