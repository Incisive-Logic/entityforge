using System.Globalization;
using EntityForge.Db.Kernel;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Graph;

internal static class GraphRootIndex
{
    internal const string Property = "graph-property-v1";

    internal static string? Value(DatabaseValue value) => value switch
    {
        DatabaseValue.String text => "s:" + text.Value,
        DatabaseValue.Int64 number => "i:" + number.Value.ToString(CultureInfo.InvariantCulture),
        DatabaseValue.Double number => "d:" + (number.Value == 0 ? "0" : number.Value.ToString("R", CultureInfo.InvariantCulture)),
        DatabaseValue.Boolean boolean => boolean.Value ? "b:1" : "b:0",
        // Other native comparison semantics are deliberately not inferred from JSON encoding.
        _ => null
    };
}

internal sealed partial class BoundedGraphPatternExecutor
{
    internal static (DatabaseRootIndexColumn Column, string Value)? RootIndexProbe(BoundedGraphPatternQuery query)
    {
        foreach (var predicate in query.Start.Predicates)
        {
            if (predicate.Operator == GraphPatternComparisonOperator.Equal && TryDatabaseValue(predicate.Value, out var value) &&
                GraphRootIndex.Value(value!) is { } key)
            {
                return (new(GraphRootIndex.Property, predicate.Property), key);
            }
        }

        return null;
    }
}

internal sealed partial class GraphProfileRuntime
{
    private readonly DatabaseRootIndexCache _rootIndexes = new();

    DatabaseRootIndexSnapshot IDatabaseTimestampProfile.CaptureRootIndexes(object view, DatabaseRootIndexColumn[] columns)
    {
        var selected = view as TimestampView ?? throw new InvalidDataException("Invalid Graph index view.");
        return new(columns.SelectMany(column => _rootIndexes.Get(selected.Graph.RootIndexIdentity(column.Field), [column],
            selectedColumn => selectedColumn.Kind == GraphRootIndex.Property
                ? DatabaseRootIndexMembership.Build(selectedColumn, selected.Graph.Vertices.Values
                    .Where(v => v.Properties.ContainsKey(selectedColumn.Field)).Select(v => GraphRootIndex.Value(v.Properties[selectedColumn.Field])).OfType<string>())
                : throw new NotSupportedException("Unknown Graph root index."))).ToArray());
    }
}
