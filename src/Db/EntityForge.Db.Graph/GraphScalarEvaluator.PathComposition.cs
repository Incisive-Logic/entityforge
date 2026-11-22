using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue DistinctPatternPaths(QueryValue[] arguments)
    {
        if (arguments.Length is < 2 or > 16 || arguments.Any(argument => argument is not PathValue))
        {
            throw new ArgumentException("Distinct path checking requires two to sixteen paths.");
        }
        var identities = new HashSet<GraphElementIdentity>();
        foreach (var edge in arguments.Cast<PathValue>().SelectMany(path => path.Elements).OfType<EdgeValue>())
        {
            if (!identities.Add(edge.Identity ?? throw new ArgumentException("Pattern edges require an identity.")))
            {
                return new LiteralValue("false", QueryScalarKind.Boolean);
            }
        }
        return new LiteralValue("true", QueryScalarKind.Boolean);
    }

    // Compiler-only operation. Public GQL is grammar-checked before lowering;
    // this operation is not a GQL function or an alternative public syntax.
    private static QueryValue JoinPatternPaths(QueryValue[] arguments)
    {
        if (arguments.Length is < 4 or > 18 ||
            arguments[0] is not LiteralValue { ScalarKind: QueryScalarKind.String } mode ||
            arguments[1] is not LiteralValue { ScalarKind: QueryScalarKind.Boolean } different ||
            mode.LexicalValue is not ("WALK" or "TRAIL" or "ACYCLIC" or "SIMPLE"))
        {
            throw new ArgumentException("Invalid composed-path operation.");
        }
        var elements = new List<QueryValue>();
        foreach (var argument in arguments.Skip(2))
        {
            if (argument is NullValue or UnboundValue)
            {
                return UnboundValue.Instance;
            }
            if (argument is not PathValue path || path.Elements.Count == 0 || path.Elements.Count % 2 == 0)
            {
                throw new ArgumentException("A composed path requires alternating node and edge values.");
            }
            for (int index = 0; index < path.Elements.Count; index++)
            {
                if (index % 2 == 0 ? path.Elements[index] is not NodeValue : path.Elements[index] is not EdgeValue)
                {
                    throw new ArgumentException("A composed path requires alternating node and edge values.");
                }
            }
            int skip = 0;
            if (elements.Count != 0)
            {
                if (((NodeValue)elements[^1]).Identity != ((NodeValue)path.Elements[0]).Identity)
                {
                    throw new ArgumentException("Composed path endpoints must identify the same node.");
                }
                skip = 1;
            }
            if (elements.Count + path.Elements.Count - skip > 10_000)
            {
                throw new ArgumentException("A composed path exceeds 10,000 elements.");
            }
            elements.AddRange(path.Elements.Skip(skip));
        }
        var edges = new HashSet<GraphElementIdentity>();
        var nodes = new HashSet<GraphElementIdentity>();
        bool uniqueEdges = different.LexicalValue == "true" || mode.LexicalValue == "TRAIL";
        bool uniqueNodes = mode.LexicalValue is "ACYCLIC" or "SIMPLE";
        for (int index = 0; index < elements.Count; index++)
        {
            if (elements[index] is EdgeValue edge && uniqueEdges &&
                !edges.Add(edge.Identity ?? throw new ArgumentException("Composed path edges require an identity.")))
            {
                return UnboundValue.Instance;
            }
            if (elements[index] is NodeValue node && uniqueNodes && !nodes.Add(node.Identity) &&
                !(mode.LexicalValue == "SIMPLE" && index == elements.Count - 1 && node.Identity == ((NodeValue)elements[0]).Identity))
            {
                return UnboundValue.Instance;
            }
        }
        return new PathValue(elements);
    }
}
