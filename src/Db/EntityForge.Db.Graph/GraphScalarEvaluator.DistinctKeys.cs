using System.Globalization;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    internal static string DistinctKey(QueryValue value) => value is MapValue or ListValue or PathValue
        ? DistinctKey(value, new IterationBudget()) : DistinctScalarKey(value);

    private static string DistinctKey(QueryValue value, IterationBudget budget)
    {
        if (value is not (MapValue or ListValue or PathValue))
        {
            budget.Consume();
            budget.Cancellation.ThrowIfCancellationRequested();
            return DistinctScalarKey(value);
        }
        var fragments = new List<string>();
        var pending = new Stack<(IEnumerator<(string? Name, QueryValue Value)> Children, int Slot, int Start)>();
        int length = 0, slot = -1, start = 0;
        void Append(string text)
        {
            if (text.Length > Int32.MaxValue - length)
            {
                throw new ArgumentException("A DISTINCT key exceeds its supported length.");
            }
            length += text.Length;
            fragments.Add(text);
        }
        void Complete(int lengthSlot, int childStart)
        {
            if (lengthSlot >= 0)
            {
                string prefix = (length - childStart).ToString(CultureInfo.InvariantCulture) + ":";
                if (prefix.Length > Int32.MaxValue - length)
                {
                    throw new ArgumentException("A DISTINCT key exceeds its supported length.");
                }
                fragments[lengthSlot] = prefix;
                length += prefix.Length;
            }
        }
        try
        {
            while (true)
            {
                budget.Consume();
                budget.Cancellation.ThrowIfCancellationRequested();
                IEnumerable<(string? Name, QueryValue Value)>? children = null;
                switch (value)
                {
                    case MapValue map:
                        // Bound the sorting allocation before enumerating keys.
                        if (map.Values.Count > 100_000)
                        {
                            throw new ArgumentException("DISTINCT key generation exceeds 100,000 examined elements.");
                        }
                        Append("map:");
                        children = map.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .Select(pair => ((string?)pair.Key, pair.Value));
                        break;
                    case ListValue list:
                        Append("list:");
                        children = list.Items.Select(item => ((string?)null, item));
                        break;
                    case PathValue path:
                        Append("path:");
                        children = path.Elements.Select(item => ((string?)null, item));
                        break;
                    default:
                        Append(DistinctScalarKey(value));
                        break;
                }
                if (children is not null)
                {
                    pending.Push((children.GetEnumerator(), slot, start));
                }
                else
                {
                    Complete(slot, start);
                }
                bool next = false;
                while (pending.TryPeek(out var frame))
                {
                    if (frame.Children.MoveNext())
                    {
                        var child = frame.Children.Current;
                        if (child.Name is { } name)
                        {
                            Append(name.Length.ToString(CultureInfo.InvariantCulture) + ":" + name);
                        }
                        slot = fragments.Count;
                        fragments.Add("");
                        start = length;
                        value = child.Value;
                        next = true;
                        break;
                    }
                    pending.Pop().Children.Dispose();
                    Complete(frame.Slot, frame.Start);
                }
                if (!next)
                {
                    // Child lengths are filled after traversal. Concatenating
                    // once avoids copying a growing key at every nesting level.
                    return String.Concat(fragments);
                }
            }
        }
        finally
        {
            while (pending.TryPop(out var frame))
            {
                frame.Children.Dispose();
            }
        }
    }
}
