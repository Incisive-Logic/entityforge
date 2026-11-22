using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue ConstructPath(QueryValue[] elements)
    {
        if (elements.Length is 0 or > 10_000 || elements.Length % 2 == 0)
        {
            throw new ArgumentException("A path constructor requires a bounded, nonempty alternating node/edge sequence.");
        }
        foreach (var element in elements)
        {
            RequireLiveEntity(element);
        }
        if (elements.Any(element => element is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        for (int index = 0; index < elements.Length; index++)
        {
            if (index % 2 == 0 ? elements[index] is not NodeValue : elements[index] is not EdgeValue)
            {
                throw new ArgumentException("A path constructor requires alternating node and edge references.");
            }
        }
        for (int index = 1; index < elements.Length; index += 2)
        {
            var edge = (EdgeValue)elements[index];
            var before = ((NodeValue)elements[index - 1]).Identity;
            var after = ((NodeValue)elements[index + 1]).Identity;
            if (!(edge.Source == before && edge.Target == after || edge.Target == before && edge.Source == after))
            {
                throw new ArgumentException("A constructed path edge must connect its adjacent nodes.");
            }
        }
        // Retain the supplied traversal order and identities. Construction does
        // not impose a path-match uniqueness mode or perform storage lookups.
        return new PathValue(elements);
    }
}
