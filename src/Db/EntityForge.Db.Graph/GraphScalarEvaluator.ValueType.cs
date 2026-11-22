using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    private sealed record ValueTypeShape(string Name, bool Nullable = false, IReadOnlyList<ValueTypeShape>? Members = null)
    {
        internal string Render() => Name == "NULL" ? "NULL" :
            (Name == "LIST" ? "LIST<" + (Members!.Count == 0 ? "NOTHING" : String.Join(" | ", Members.Select(member => member.Render()))) + ">" : Name) +
            (Nullable ? "" : " NOT NULL");
    }

    private static ValueTypeShape DescribeValueType(QueryValue value, IterationBudget budget)
    {
        budget.Consume();
        if (value is ListValue list)
        {
            var members = list.Items.Select(item => DescribeValueType(item, budget)).ToArray();
            bool nullable = members.Any(member => member.Name == "NULL");
            var normalized = members.Where(member => member.Name != "NULL").Select(member => member with { Nullable = nullable || member.Nullable })
                .DistinctBy(member => member.Render(), StringComparer.Ordinal).ToArray();
            if (normalized.Length == 0 && nullable)
            {
                normalized = [new("NULL", true)];
            }
            normalized = normalized.Where(member => !normalized.Any(other => !ReferenceEquals(member, other) && TypeContains(other, member, budget))).ToArray();
            Array.Sort(normalized, CompareTypeShape);
            return new("LIST", Members: normalized);
        }
        string name = value switch
        {
            NullValue or UnboundValue => "NULL",
            NodeValue => "NODE",
            EdgeValue => "RELATIONSHIP",
            MapValue => "MAP",
            PathValue => "PATH",
            LiteralValue literal => literal.ScalarKind switch
            {
                QueryScalarKind.Boolean => "BOOLEAN",
                QueryScalarKind.String => "STRING",
                QueryScalarKind.Integer => "INTEGER",
                QueryScalarKind.Guid => "UUID",
                QueryScalarKind.FloatingPoint => "FLOAT",
                QueryScalarKind.Decimal => "DECIMAL",
                QueryScalarKind.Date => "DATE",
                QueryScalarKind.ZonedTime => "ZONED TIME",
                QueryScalarKind.LocalTime => "LOCAL TIME",
                QueryScalarKind.ZonedDateTime => "ZONED DATETIME",
                QueryScalarKind.LocalDateTime => "LOCAL DATETIME",
                QueryScalarKind.Duration => "DURATION",
                QueryScalarKind.Point => "POINT",
                _ => throw new ArgumentException("valueType requires a supported GQL value.")
            },
            _ => throw new ArgumentException("valueType requires a supported GQL value.")
        };
        return new(name, name == "NULL");
    }

    private static bool TypeContains(ValueTypeShape broad, ValueTypeShape narrow, IterationBudget budget)
    {
        budget.Consume();
        return narrow.Name == "NULL" ? broad.Nullable : broad.Name == narrow.Name && (!narrow.Nullable || broad.Nullable) &&
            (broad.Name != "LIST" || narrow.Members!.All(item => broad.Members!.Any(other => TypeContains(other, item, budget))));
    }

    private static int CompareTypeShape(ValueTypeShape left, ValueTypeShape right)
    {
        static int Rank(string name) => name switch
        {
            "NULL" => 0,
            "BOOLEAN" => 1,
            "STRING" => 2,
            "UUID" => 3,
            "INTEGER" => 4,
            "FLOAT" => 5,
            "DECIMAL" => 6,
            "DATE" => 7,
            "LOCAL TIME" => 8,
            "ZONED TIME" => 9,
            "LOCAL DATETIME" => 10,
            "ZONED DATETIME" => 11,
            "DURATION" => 12,
            "POINT" => 13,
            "NODE" => 14,
            "RELATIONSHIP" => 15,
            "MAP" => 16,
            "LIST" => 17,
            "PATH" => 18,
            _ => throw new ArgumentException("Unknown GQL value type.")
        };
        int comparison = Rank(left.Name).CompareTo(Rank(right.Name));
        if (comparison == 0 && left.Name == "LIST")
        {
            comparison = left.Members!.Count.CompareTo(right.Members!.Count);
            for (int index = 0; comparison == 0 && index < left.Members.Count; index++)
            {
                comparison = CompareTypeShape(left.Members[index], right.Members[index]);
            }
        }
        return comparison != 0 ? comparison : left.Nullable.CompareTo(right.Nullable);
    }
}
