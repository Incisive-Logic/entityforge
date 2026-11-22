using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue Split(QueryValue[] values)
    {
        if (values.Length != 2)
        {
            throw new ArgumentException("SPLIT requires a string and delimiter or delimiter list.");
        }
        if (values.Any(value => value is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        var delimiters = values[1] is ListValue list ? list.Items : [values[1]];
        if (delimiters.Any(value => value is NullValue or UnboundValue))
        {
            return UnboundValue.Instance;
        }
        if (values[0] is not LiteralValue { ScalarKind: QueryScalarKind.String } input ||
            delimiters.Any(value => value is not LiteralValue { ScalarKind: QueryScalarKind.String }))
        {
            throw new ArgumentException("SPLIT accepts only strings and string delimiters.");
        }
        string source = input.LexicalValue;
        string[] separators = delimiters.Cast<LiteralValue>().Select(value => value.LexicalValue).ToArray();
        IEnumerable<QueryValue> Parts()
        {
            int start = 0, position = 0;
            long work = 0;
            while (position < source.Length)
            {
                string? separator = null;
                foreach (string candidate in separators)
                {
                    work += Math.Max(1, candidate.Length);
                    if (work > 1_048_576)
                    {
                        throw new ArgumentException("SPLIT exceeds its character-comparison work limit.");
                    }
                    if (source.AsSpan(position).StartsWith(candidate, StringComparison.Ordinal))
                    {
                        separator = candidate;
                        break;
                    }
                }
                if (separator is not null)
                {
                    if (separator.Length != 0 || position > start)
                    {
                        yield return new LiteralValue(source[start..position]);
                    }
                    start = position + separator.Length;
                    if (separator.Length != 0)
                    {
                        position = start;
                        continue;
                    }
                }
                position += Char.IsSurrogatePair(source, position) ? 2 : 1;
            }
            yield return new LiteralValue(source[start..]);
        }
        return BoundedList(Parts());
    }
}
