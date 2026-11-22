using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private static int CompareOrderedLists(ListValue left, ListValue right) =>
        CompareOrderedLists(left, right, CompareListItemsForOrder);

    internal static int CompareOrderedLists(ListValue left, ListValue right,
        Func<QueryValue, QueryValue, int> compareItems)
    {
        var pending = new Stack<(ListValue Left, ListValue Right, int Index)>();
        var budget = new IterationBudget();
        pending.Push((left, right, 0));
        while (pending.TryPop(out var frame))
        {
            budget.Consume();
            int count = Math.Min(frame.Left.Items.Count, frame.Right.Items.Count);
            if (frame.Index == count)
            {
                int lengths = frame.Left.Items.Count.CompareTo(frame.Right.Items.Count);
                if (lengths != 0)
                {
                    return lengths;
                }
                continue;
            }
            var first = frame.Left.Items[frame.Index];
            var second = frame.Right.Items[frame.Index];
            pending.Push((frame.Left, frame.Right, frame.Index + 1));
            if (first is ListValue firstList && second is ListValue secondList)
            {
                pending.Push((firstList, secondList, 0));
            }
            else
            {
                int comparison = compareItems(first, second);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
        }
        return 0;
    }
}
