using EntityForge.Query.Values;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue EvaluateMapProjection(GraphScalarExpression expression,
        Func<string, string, QueryValue> property, IterationBudget budget) =>
        EvaluateMapProjectionAsync(expression, property, (item, scope) => new(EvaluateCore(item, scope, budget))).GetAwaiter().GetResult();

    internal static async ValueTask<QueryValue> EvaluateMapProjectionAsync(GraphScalarExpression expression,
        Func<string, string, QueryValue> property,
        Func<GraphScalarExpression, Func<string, string, QueryValue>, ValueTask<QueryValue>> evaluate)
    {
        var arguments = expression.Arguments!;
        if (arguments.Count < 2 || arguments.Count % 2 != 0)
        {
            throw new ArgumentException("A map projection requires a source and field/value pairs.");
        }
        QueryValue source = await evaluate(arguments[0], property).ConfigureAwait(false);
        if (source is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        _ = TryEvaluateMap("PROPERTIES", [source], out QueryValue all);
        var fields = Boolean(await evaluate(arguments[1], property).ConfigureAwait(false)) == true
            ? new Dictionary<string, QueryValue>(((MapValue)all).Values, StringComparer.Ordinal)
            : new Dictionary<string, QueryValue>(StringComparer.Ordinal);
        for (int index = 2; index < arguments.Count; index += 2)
        {
            if (await evaluate(arguments[index], property).ConfigureAwait(false) is not LiteralValue { ScalarKind: QueryScalarKind.String } key)
            {
                throw new ArgumentException("A map projection key must be a string.");
            }
            fields[key.LexicalValue] = await evaluate(arguments[index + 1], property).ConfigureAwait(false);
        }
        var result = new MapValue(fields);
        _ = BoundedList([result]);
        return result;
    }

    private static bool TryEvaluateMap(string function, QueryValue[] values, out QueryValue result)
    {
        result = UnboundValue.Instance;
        if (function == "MAP")
        {
            if (values.Length % 2 != 0)
            {
                throw new ArgumentException("A map requires key/value pairs.");
            }
            var entries = new List<KeyValuePair<string, QueryValue>>();
            for (int i = 0; i < values.Length; i += 2)
            {
                if (values[i] is not LiteralValue { ScalarKind: QueryScalarKind.String } key)
                {
                    throw new ArgumentException("Map keys must be strings.");
                }
                entries.Add(new(key.LexicalValue, values[i + 1]));
            }
            var map = new MapValue(entries);
            _ = BoundedList([map]);
            result = map;
            return true;
        }
        if (function is not ("KEYS" or "PROPERTIES") &&
            !(function == "INDEX" && values.Length > 0 && values[0] is MapValue or NodeValue or EdgeValue) &&
            !(function == "ISEMPTY" && values.Length > 0 && values[0] is MapValue))
        {
            return false;
        }
        if (values.Length != (function == "INDEX" ? 2 : 1))
        {
            throw new ArgumentException($"Invalid argument count for {function}.");
        }
        if (values.Any(value => value is NullValue or UnboundValue))
        {
            return true;
        }
        IReadOnlyDictionary<string, QueryValue> properties;
        if (values[0] is MapValue source)
        {
            properties = source.Values;
        }
        else
        {
            RequireLiveEntity(values[0]);
            var assertions = values[0] switch
            {
                NodeValue node => node.Properties,
                EdgeValue edge => edge.Properties,
                _ => throw new ArgumentException($"{function} requires a map, node or relationship.")
            };
            const string prefix = "urn:entityforge:graph:property:";
            properties = assertions.Where(item => item.SourceTerm.Identifier.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(item => Uri.UnescapeDataString(item.SourceTerm.Identifier.AbsoluteUri[prefix.Length..]), item => item.Value, StringComparer.Ordinal);
        }
        result = function switch
        {
            "KEYS" => BoundedList(properties.Keys.Select(key => new LiteralValue(key))),
            "PROPERTIES" => new MapValue(properties),
            "ISEMPTY" => new LiteralValue(properties.Count == 0 ? "true" : "false", QueryScalarKind.Boolean),
            _ => values[1] is LiteralValue { ScalarKind: QueryScalarKind.String } key
                ? properties.GetValueOrDefault(key.LexicalValue, UnboundValue.Instance)
                : throw new ArgumentException("Map and entity lookup requires a string key.")
        };
        return true;
    }
}
