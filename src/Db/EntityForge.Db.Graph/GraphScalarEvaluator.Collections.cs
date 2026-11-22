using System.Globalization;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    internal const int MaximumListCardinality = 10_000;

    internal static void ValidateListCardinality(int count)
    {
        if (count > MaximumListCardinality)
        {
            throw GqlDataError("A list value exceeds its maximum cardinality of 10,000.", "2202F");
        }
    }

    private static int CompareListItemsForOrder(QueryValue left, QueryValue right)
    {
        int Rank(QueryValue value) => value switch
        {
            ListValue => 0,
            LiteralValue { ScalarKind: QueryScalarKind.Point } => 2,
            LiteralValue { ScalarKind: QueryScalarKind.ZonedDateTime } => 3,
            LiteralValue { ScalarKind: QueryScalarKind.LocalDateTime } => 4,
            LiteralValue { ScalarKind: QueryScalarKind.Date } => 6,
            LiteralValue { ScalarKind: QueryScalarKind.ZonedTime } => 7,
            LiteralValue { ScalarKind: QueryScalarKind.LocalTime } => 8,
            LiteralValue { ScalarKind: QueryScalarKind.Duration } => 10,
            LiteralValue { ScalarKind: QueryScalarKind.String } => 12,
            LiteralValue { ScalarKind: QueryScalarKind.Boolean } => 14,
            LiteralValue { ScalarKind: QueryScalarKind.Integer or QueryScalarKind.Decimal or QueryScalarKind.FloatingPoint } => 16,
            NullValue or UnboundValue => 18,
            _ => throw new ArgumentException("Unsupported list ordering value.")
        };
        int rank = Rank(left).CompareTo(Rank(right));
        return rank != 0 ? rank : CompareScalarForOrder(left, right);
    }

    internal static ListValue BoundedList(IEnumerable<QueryValue> source)
    {
        var items = new List<QueryValue>();
        long count = 0, characters = 0;
        var pending = new Stack<QueryValue>();
        void Count(QueryValue value)
        {
            pending.Push(value);
            while (pending.TryPop(out var current))
            {
                if (++count > 10_000)
                {
                    throw new ArgumentException("A list expression exceeds 10,000 total elements.");
                }
                // Bound queued children before allocating space for them. The
                // iterative walk also handles deep programmatic values without
                // consuming a call-stack frame for every nested collection.
                int children = current switch
                {
                    ListValue list => list.Items.Count,
                    MapValue map => map.Values.Count,
                    _ => 0
                };
                if (count + pending.Count + children > 10_000)
                {
                    throw new ArgumentException("A list expression exceeds 10,000 total elements.");
                }
                if (current is ListValue listValue)
                {
                    for (int index = listValue.Items.Count - 1; index >= 0; index--)
                    {
                        pending.Push(listValue.Items[index]);
                    }
                }
                else if (current is MapValue mapValue)
                {
                    foreach (var pair in mapValue.Values)
                    {
                        characters += pair.Key.Length;
                        pending.Push(pair.Value);
                    }
                }
                else if (current is LiteralValue literal)
                {
                    characters += literal.LexicalValue.Length;
                }
                if (characters > 1_048_576)
                {
                    throw new ArgumentException("A list expression exceeds 1,048,576 characters.");
                }
            }
        }
        foreach (var item in source)
        {
            ValidateListCardinality(items.Count + 1);
            Count(item);
            if (characters > 1_048_576)
            {
                throw new ArgumentException("A collection expression exceeds 1,048,576 characters.");
            }
            items.Add(item);
        }
        return new ListValue(items);
    }

    private static bool? CollectionEquality(QueryValue left, QueryValue right, IterationBudget budget)
    {
        static IEnumerable<(QueryValue Left, QueryValue Right)> Pairs(
            IReadOnlyList<QueryValue> first, IReadOnlyList<QueryValue> second)
        {
            for (int index = 0; index < first.Count; index++)
            {
                yield return (first[index], second[index]);
            }
        }
        static IEnumerable<(QueryValue Left, QueryValue Right)> Fields(MapValue first, MapValue second)
        {
            foreach (var pair in first.Values)
            {
                yield return (pair.Value, second.Values[pair.Key]);
            }
        }

        // Enumerate depth-first in the original order, preserving both null
        // propagation and early inequality without recursively consuming stack.
        var pending = new Stack<IEnumerator<(QueryValue Left, QueryValue Right)>>();
        bool? result = true;
        try
        {
            while (true)
            {
                budget.Consume();
                budget.Cancellation.ThrowIfCancellationRequested();
                IEnumerable<(QueryValue Left, QueryValue Right)>? children = null;
                bool? equal = true;
                if (left is NullValue or UnboundValue || right is NullValue or UnboundValue)
                {
                    equal = null;
                }
                else if (left is MapValue firstMap && right is MapValue secondMap)
                {
                    if (firstMap.Values.Count != secondMap.Values.Count)
                    {
                        return false;
                    }
                    foreach (string key in firstMap.Values.Keys)
                    {
                        budget.Consume();
                        if (!secondMap.Values.ContainsKey(key))
                        {
                            return false;
                        }
                    }
                    children = Fields(firstMap, secondMap);
                }
                else if (left is ListValue a && right is ListValue b)
                {
                    if (a.Items.Count != b.Items.Count)
                    {
                        return false;
                    }
                    children = Pairs(a.Items, b.Items);
                }
                else if (left is PathValue firstPath && right is PathValue secondPath)
                {
                    if (firstPath.Elements.Count != secondPath.Elements.Count)
                    {
                        return false;
                    }
                    children = Pairs(firstPath.Elements, secondPath.Elements);
                }
                else if (left is NodeValue aNode && right is NodeValue bNode)
                {
                    equal = aNode.Identity == bNode.Identity;
                }
                else if (left is EdgeValue aEdge && right is EdgeValue bEdge)
                {
                    equal = aEdge.Identity == bEdge.Identity;
                }
                else if (CompareNumbers(left, right, out int comparison))
                {
                    equal = comparison == 0 && !IsNaN(left) && !IsNaN(right);
                }
                else if (left is LiteralValue { ScalarKind: QueryScalarKind.Duration } leftDuration &&
                    right is LiteralValue { ScalarKind: QueryScalarKind.Duration } rightDuration)
                {
                    equal = CompareDurationValues(leftDuration, rightDuration) == 0;
                }
                else
                {
                    equal = left is LiteralValue first && right is LiteralValue second &&
                        first.ScalarKind == second.ScalarKind && first.LexicalValue == second.LexicalValue;
                }
                result &= equal;
                if (result == false)
                {
                    return false;
                }
                if (children is not null)
                {
                    pending.Push(children.GetEnumerator());
                }
                bool next = false;
                while (pending.TryPeek(out var iterator))
                {
                    if (iterator.MoveNext())
                    {
                        (left, right) = iterator.Current;
                        next = true;
                        break;
                    }
                    pending.Pop().Dispose();
                }
                if (!next)
                {
                    return result;
                }
            }
        }
        finally
        {
            while (pending.TryPop(out var iterator))
            {
                iterator.Dispose();
            }
        }
    }

    private static bool TryEvaluateCollection(string function, QueryValue[] values, IterationBudget budget, out QueryValue result)
    {
        result = UnboundValue.Instance;
        if (function.StartsWith("COLL.", StringComparison.Ordinal))
        {
            result = EvaluateCollectionFunction(function, values, budget);
            return true;
        }
        QueryValue Truth(bool? truth) => truth.HasValue
            ? new LiteralValue(truth.Value ? "true" : "false", QueryScalarKind.Boolean) : UnboundValue.Instance;
        long Integer(QueryValue value)
        {
            if (value is not LiteralValue { ScalarKind: QueryScalarKind.Integer } integer ||
                !Int64.TryParse(integer.LexicalValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
            {
                throw new ArgumentException("List positions and range arguments require signed 64-bit integers.");
            }
            return number;
        }
        if (function == "TRIMLIST")
        {
            if (values.Any(value => value is NullValue or UnboundValue))
            {
                return true;
            }
            if (values[0] is not ListValue list)
            {
                throw new ArgumentException("List TRIM requires a list and an integer count.");
            }
            long count = Integer(values[1]);
            if (count < 0 || count > list.Items.Count)
            {
                throw GqlDataError("List TRIM count must be between zero and the list length.", "2202E");
            }
            result = BoundedList(list.Items.Take(list.Items.Count - (int)count));
            return true;
        }
        if (function == "CARDINALITY")
        {
            if (values[0] is NullValue or UnboundValue)
            {
                return true;
            }
            int count = values[0] switch
            {
                ListValue list => list.Items.Count,
                MapValue map => map.Values.Count,
                PathValue path => path.Elements.Count,
                _ => throw new ArgumentException("CARDINALITY requires a list, map or path.")
            };
            result = new LiteralValue(count.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer);
            return true;
        }
        if (function == "LIST")
        {
            result = BoundedList(values);
            return true;
        }
        if (function == "IN")
        {
            if (values[1] is NullValue or UnboundValue)
            {
                return true;
            }
            if (values[1] is not ListValue list)
            {
                throw new ArgumentException("IN requires a list.");
            }
            bool? found = false;
            foreach (var item in list.Items)
            {
                found |= CollectionEquality(values[0], item, budget);
                if (found == true)
                {
                    break;
                }
            }
            result = Truth(found);
            return true;
        }
        if (function is "=" or "<>" or "!=" && values.Any(value => value is ListValue or MapValue or NodeValue or EdgeValue or PathValue))
        {
            bool? equal = CollectionEquality(values[0], values[1], budget);
            result = Truth(function == "=" ? equal : !equal);
            return true;
        }
        if (function is "+" or "||" && values.Any(value => value is ListValue))
        {
            if (function == "+")
            {
                throw new ArgumentException("List concatenation requires ||.");
            }
            if (values.Any(value => value is NullValue or UnboundValue))
            {
                return true;
            }
            if (function == "||" && values.Any(value => value is not ListValue))
            {
                throw new ArgumentException("List concatenation requires two lists.");
            }
            result = BoundedList((values[0] is ListValue a ? a.Items : [values[0]])
                .Concat(values[1] is ListValue b ? b.Items : [values[1]]));
            return true;
        }
        if (function == "RANGE")
        {
            if (values.Any(value => value is NullValue or UnboundValue))
            {
                return true;
            }
            long start = Integer(values[0]), end = Integer(values[1]), step = values.Length == 3 ? Integer(values[2]) : 1;
            if (step == 0)
            {
                throw new ArgumentException("RANGE step cannot be zero.");
            }
            Int128 count = step > 0 && end >= start || step < 0 && end <= start ? ((Int128)end - start) / step + 1 : 0;
            if (count > 10_000)
            {
                throw new ArgumentException("RANGE exceeds 10,000 elements.");
            }
            result = BoundedList(Enumerable.Range(0, (int)count).Select(index =>
                new LiteralValue(((long)((Int128)start + (Int128)index * step)).ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer)));
            return true;
        }
        if (function is "INDEX" or "SLICE" or "HEAD" or "LAST" or "TAIL" ||
            function is "SIZE" or "REVERSE" or "ISEMPTY" && values[0] is ListValue)
        {
            if (values.Any(value => value is NullValue or UnboundValue))
            {
                return true;
            }
            if (values[0] is not ListValue list)
            {
                throw new ArgumentException($"{function} requires a list.");
            }
            int count = list.Items.Count;
            long Position(QueryValue value)
            {
                long index = Integer(value);
                return index < 0 ? (long)count + index : index;
            }
            result = function switch
            {
                "SIZE" => new LiteralValue(count.ToString(CultureInfo.InvariantCulture), QueryScalarKind.Integer),
                "ISEMPTY" => Truth(count == 0),
                "HEAD" => count == 0 ? UnboundValue.Instance : list.Items[0],
                "LAST" => count == 0 ? UnboundValue.Instance : list.Items[^1],
                "TAIL" => BoundedList(list.Items.Skip(1)),
                "REVERSE" => BoundedList(list.Items.Reverse()),
                "INDEX" => Position(values[1]) is var index && index >= 0 && index < count ? list.Items[(int)index] : UnboundValue.Instance,
                _ => BoundedList(list.Items.Skip((int)Math.Clamp(Position(values[1]), 0, count))
                    .Take((int)Math.Max(0, Math.Clamp(Position(values[2]), 0, count) - Math.Clamp(Position(values[1]), 0, count))))
            };
            return true;
        }
        if (function == "ISEMPTY")
        {
            if (values[0] is NullValue or UnboundValue)
            {
                return true;
            }
            if (values[0] is not LiteralValue { ScalarKind: QueryScalarKind.String } text)
            {
                throw new ArgumentException("ISEMPTY requires a string or list.");
            }
            result = Truth(text.LexicalValue.Length == 0);
            return true;
        }
        return false;
    }
}
