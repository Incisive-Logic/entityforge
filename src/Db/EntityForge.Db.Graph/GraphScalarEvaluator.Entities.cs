using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static bool TryEvaluateEntityInspection(string function, QueryValue[] arguments, out QueryValue result)
    {
        result = UnboundValue.Instance;
        if (function == "__GQL_PATH_CONSTRUCT")
        {
            result = ConstructPath(arguments);
            return true;
        }
        if (function == "__GQL_PATHS_DISTINCT")
        {
            result = DistinctPatternPaths(arguments);
            return true;
        }
        if (function == "__GQL_PATH_JOIN")
        {
            result = JoinPatternPaths(arguments);
            return true;
        }
        if (function is "ISDIRECTED" or "ISSOURCE" or "ISDESTINATION")
        {
            if (arguments.Length != (function == "ISDIRECTED" ? 1 : 2))
            {
                throw new ArgumentException("Invalid graph endpoint predicate arity.");
            }
            foreach (var argument in arguments)
            {
                RequireLiveEntity(argument);
            }
            if (arguments.Any(argument => argument is NullValue or UnboundValue))
            {
                return true;
            }
            bool matches;
            if (function == "ISDIRECTED")
            {
                if (arguments[0] is not EdgeValue)
                {
                    throw new ArgumentException("IS DIRECTED requires an edge.");
                }
                // The native graph profile stores directed edges only.
                matches = true;
            }
            else
            {
                if (arguments[0] is not NodeValue node || arguments[1] is not EdgeValue edge)
                {
                    throw new ArgumentException("An endpoint predicate requires a node and an edge.");
                }
                matches = Equals(node.Identity, function == "ISSOURCE" ? edge.Source : edge.Target);
            }
            result = new LiteralValue(matches ? "true" : "false", QueryScalarKind.Boolean);
            return true;
        }
        if (function == "PROPERTY_EXISTS")
        {
            if (arguments[0] is NullValue or UnboundValue)
            {
                return true;
            }
            if (arguments[0] is not (NodeValue or EdgeValue))
            {
                throw new ArgumentException("PROPERTY_EXISTS requires a node or relationship.");
            }
            _ = TryEvaluateMap("INDEX", arguments, out var value);
            result = new LiteralValue(value is NullValue or UnboundValue ? "false" : "true", QueryScalarKind.Boolean);
            return true;
        }
        if (function is "HASLABEL" or "HASLABEL_EXACT" && arguments.Length == 2 && arguments[1] is LiteralValue { ScalarKind: QueryScalarKind.String } label)
        {
            RequireLiveEntity(arguments[0]);
            if (arguments[0] is NullValue or UnboundValue)
            {
                return true;
            }
            bool wildcard = function == "HASLABEL" && label.LexicalValue == "%";
            bool matched = arguments[0] switch
            {
                NodeValue node => wildcard ? node.Labels.Count != 0 : node.Labels.Contains(label.LexicalValue, StringComparer.Ordinal),
                EdgeValue edge => wildcard || edge.Relationship.SourceTerm.Identifier.AbsoluteUri ==
                    "urn:entityforge:graph:relationship:" + Uri.EscapeDataString(label.LexicalValue),
                _ => throw new ArgumentException("A label predicate requires a node or relationship.")
            };
            result = new LiteralValue(matched ? "true" : "false", QueryScalarKind.Boolean);
            return true;
        }
        if (function is not ("LABELS" or "TYPE" or "ELEMENTID" or "LENGTH" or "PATH_LENGTH" or "ELEMENTS" or "NODES" or "RELATIONSHIPS"))
        {
            return false;
        }
        if (arguments.Length != 1)
        {
            throw new ArgumentException($"{function} requires one argument.");
        }
        if (arguments[0] is NullValue or UnboundValue)
        {
            return true;
        }
        const string relationshipPrefix = "urn:entityforge:graph:relationship:";
        if (function is "LABELS" or "TYPE")
        {
            RequireLiveEntity(arguments[0]);
        }
        result = (function, arguments[0]) switch
        {
            ("LENGTH" or "PATH_LENGTH", PathValue path) => new LiteralValue(((path.Elements.Count - 1) / 2).ToString(System.Globalization.CultureInfo.InvariantCulture), QueryScalarKind.Integer),
            ("ELEMENTS", PathValue path) => BoundedList(path.Elements),
            ("NODES", PathValue path) => BoundedList(path.Elements.OfType<NodeValue>()),
            ("RELATIONSHIPS", PathValue path) => BoundedList(path.Elements.OfType<EdgeValue>()),
            ("LABELS", NodeValue node) => BoundedList(node.Labels.Select(label => new LiteralValue(label))),
            ("TYPE", EdgeValue edge) when edge.Relationship.SourceTerm.Identifier.AbsoluteUri.StartsWith(relationshipPrefix, StringComparison.Ordinal)
                => new LiteralValue(Uri.UnescapeDataString(edge.Relationship.SourceTerm.Identifier.AbsoluteUri[relationshipPrefix.Length..])),
            ("ELEMENTID", NodeValue { Identity: IdentifierValue id }) => new LiteralValue(id.Identifier),
            ("ELEMENTID", EdgeValue { Identity: IdentifierValue id }) => new LiteralValue(id.Identifier),
            _ => throw new ArgumentException($"{function} requires a compatible graph entity.")
        };
        return true;
    }
}
