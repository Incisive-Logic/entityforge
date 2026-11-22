using System.Globalization;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static QueryValue EvaluateCollectionFunction(string function, QueryValue[] arguments, IterationBudget budget)
    {
        if (arguments[0] is NullValue or UnboundValue ||
            arguments.Length > 1 && arguments[1] is NullValue or UnboundValue)
        {
            return UnboundValue.Instance;
        }
        if (arguments[0] is not ListValue list)
        {
            throw new ArgumentException($"{function} requires a list.");
        }
        long Index(QueryValue value)
        {
            if (value is not LiteralValue { ScalarKind: QueryScalarKind.Integer } integer ||
                !Int64.TryParse(integer.LexicalValue, CultureInfo.InvariantCulture, out long index) || index < 0)
            {
                throw new ArgumentException($"{function} requires a non-negative integer position or depth.");
            }
            return index;
        }
        switch (function)
        {
            case "COLL.DISTINCT":
                return BoundedList(list.Items.DistinctBy(value => DistinctKey(value, budget)));
            case "COLL.FLATTEN":
                return BoundedList(Flatten(list, arguments.Length == 1 ? 1 : Index(arguments[1])));
            case "COLL.INDEXOF":
                int position = -1;
                for (int index = 0; index < list.Items.Count; index++)
                {
                    if (CollectionEquality(list.Items[index], arguments[1], budget) == true)
                    {
                        position = index;
                        break;
                    }
                }
                return new LiteralValue(position.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            case "COLL.INSERT":
            case "COLL.REMOVE":
                long selected = Index(arguments[1]);
                if (selected > list.Items.Count || function == "COLL.REMOVE" && selected == list.Items.Count)
                {
                    throw new ArgumentException($"{function} position is outside the list.");
                }
                int offset = (int)selected;
                return function == "COLL.INSERT"
                    ? BoundedList(list.Items.Take(offset).Append(arguments[2]).Concat(list.Items.Skip(offset)))
                    : BoundedList(list.Items.Take(offset).Concat(list.Items.Skip(offset + 1)));
            case "COLL.SORT":
                ValidateListCardinality(list.Items.Count);
                return BoundedList(list.Items.Order(Comparer<QueryValue>.Create((left, right) => CompareCollectionItems(left, right, budget))));
            case "COLL.MIN":
            case "COLL.MAX":
                QueryValue? best = null;
                foreach (var item in list.Items)
                {
                    budget.Consume();
                    if (best is null || (function == "COLL.MIN" ? CompareCollectionItems(item, best, budget) < 0 : CompareCollectionItems(item, best, budget) > 0))
                    {
                        best = item;
                    }
                }
                return best ?? UnboundValue.Instance;
            default:
                throw new ArgumentException("Unsupported collection function.");
        }

        IEnumerable<QueryValue> Flatten(ListValue input, long depth)
        {
            var pending = new Stack<(IEnumerator<QueryValue> Items, long Depth)>();
            pending.Push((input.Items.GetEnumerator(), depth));
            try
            {
                while (pending.TryPeek(out var frame))
                {
                    if (!frame.Items.MoveNext())
                    {
                        pending.Pop().Items.Dispose();
                        continue;
                    }
                    budget.Consume();
                    var item = frame.Items.Current;
                    if (frame.Depth > 0 && item is ListValue nested)
                    {
                        pending.Push((nested.Items.GetEnumerator(), frame.Depth - 1));
                    }
                    else
                    {
                        yield return item;
                    }
                }
            }
            finally
            {
                while (pending.TryPop(out var frame))
                {
                    frame.Items.Dispose();
                }
            }
        }
    }

    private static int CompareCollectionItems(QueryValue left, QueryValue right, IterationBudget budget)
    {
        static int Rank(QueryValue value) => value switch
        {
            MapValue => 0,
            NodeValue => 2,
            EdgeValue => 4,
            ListValue => 6,
            PathValue => 8,
            LiteralValue { ScalarKind: QueryScalarKind.Point } => 10,
            LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } => 11,
            LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } => 12,
            LiteralValue { ScalarKind: QueryScalarKind.Date } => 14,
            LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } => 15,
            LiteralValue { ScalarKind: QueryScalarKind.LocalTime } => 16,
            LiteralValue { ScalarKind: QueryScalarKind.Duration } => 18,
            LiteralValue { ScalarKind: QueryScalarKind.String } => 20,
            LiteralValue { ScalarKind: QueryScalarKind.Boolean } => 22,
            LiteralValue { ScalarKind: QueryScalarKind.Guid } => 23,
            LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } => 24,
            NullValue or UnboundValue => 26,
            _ => throw new ArgumentException("Unsupported collection ordering value.")
        };
        var pending = new Stack<(IEnumerator<(QueryValue First, QueryValue Second)> Items, int LengthOrder)>();
        pending.Push((Pairs([left], [right]).GetEnumerator(), 0));
        try
        {
            while (pending.TryPeek(out var frame))
            {
                if (!frame.Items.MoveNext())
                {
                    pending.Pop().Items.Dispose();
                    if (frame.LengthOrder != 0)
                    {
                        return frame.LengthOrder;
                    }
                    continue;
                }
                budget.Consume();
                (left, right) = frame.Items.Current;
                int compared = Rank(left).CompareTo(Rank(right));
                if (compared != 0)
                {
                    return compared;
                }
                if (left is MapValue a && right is MapValue b)
                {
                    compared = a.Values.Count.CompareTo(b.Values.Count);
                    if (compared != 0)
                    {
                        return compared;
                    }
                    // Charge before allocating sorted field arrays, even when
                    // their values would compare equal or no output is produced.
                    for (int index = 0; index < a.Values.Count; index++)
                    {
                        budget.Consume();
                    }
                    var first = a.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                    var second = b.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                    for (int index = 0; index < first.Length; index++)
                    {
                        compared = StringComparer.Ordinal.Compare(first[index].Key, second[index].Key);
                        if (compared != 0)
                        {
                            return compared;
                        }
                    }
                    pending.Push((first.Zip(second, (x, y) => (x.Value, y.Value)).GetEnumerator(), 0));
                }
                else if (left is ListValue firstList && right is ListValue secondList)
                {
                    Push(firstList.Items, secondList.Items);
                }
                else if (left is PathValue firstPath && right is PathValue secondPath)
                {
                    Push(firstPath.Elements, secondPath.Elements);
                }
                else
                {
                    compared = (left, right) switch
                    {
                        (NodeValue nodeA, NodeValue nodeB) => StringComparer.Ordinal.Compare(nodeA.Identity.ToString(), nodeB.Identity.ToString()),
                        (EdgeValue edgeA, EdgeValue edgeB) => StringComparer.Ordinal.Compare(edgeA.Identity?.ToString(), edgeB.Identity?.ToString()),
                        _ => CompareForOrder(left, right)
                    };
                    if (compared != 0)
                    {
                        return compared;
                    }
                }
            }
            return 0;
        }
        finally
        {
            while (pending.TryPop(out var frame))
            {
                frame.Items.Dispose();
            }
        }

        void Push(IReadOnlyList<QueryValue> first, IReadOnlyList<QueryValue> second) =>
            pending.Push((Pairs(first, second).GetEnumerator(), first.Count.CompareTo(second.Count)));

        static IEnumerable<(QueryValue First, QueryValue Second)> Pairs(IReadOnlyList<QueryValue> first, IReadOnlyList<QueryValue> second)
        {
            for (int index = 0; index < Math.Min(first.Count, second.Count); index++)
            {
                yield return (first[index], second[index]);
            }
        }
    }
}
