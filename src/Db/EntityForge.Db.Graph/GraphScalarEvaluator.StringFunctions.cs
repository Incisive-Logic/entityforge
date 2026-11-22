using System.Globalization;
using System.Text;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue EvaluateStringFunction(string function, QueryValue[] arguments)
    {
        if (arguments.Any(value => value is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        if (arguments[1] is not LiteralValue { ScalarKind: QueryScalarKind.String } argument)
        {
            throw new ArgumentException($"{function} requires a string search value or delimiter.");
        }
        if (function == "STRING.INDEXOF")
        {
            if (arguments[0] is not LiteralValue { ScalarKind: QueryScalarKind.String } input)
            {
                throw new ArgumentException("string.indexOf requires string operands.");
            }
            int index = input.LexicalValue.IndexOf(argument.LexicalValue, StringComparison.Ordinal);
            int position = index < 0 ? -1 : 0;
            if (index >= 0)
            {
                foreach (var rune in input.LexicalValue.AsSpan(0, index).EnumerateRunes())
                {
                    position++;
                }
            }
            return new LiteralValue(position.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
        }
        if (arguments[0] is not ListValue list)
        {
            throw new ArgumentException("string.join requires a list of strings.");
        }
        var result = new StringBuilder();
        bool first = true;
        foreach (var item in list.Items)
        {
            if (item is NullValue or UnboundValue)
            {
                continue;
            }
            if (item is not LiteralValue { ScalarKind: QueryScalarKind.String } text)
            {
                throw new ArgumentException("string.join list elements must be strings or null.");
            }
            long length = (long)result.Length + text.LexicalValue.Length + (first ? 0 : argument.LexicalValue.Length);
            if (length > 1_048_576)
            {
                throw new ArgumentException("GQL string expression exceeds 1,048,576 characters.");
            }
            if (!first)
            {
                result.Append(argument.LexicalValue);
            }
            result.Append(text.LexicalValue);
            first = false;
        }
        return new LiteralValue(result.ToString());
    }
}
