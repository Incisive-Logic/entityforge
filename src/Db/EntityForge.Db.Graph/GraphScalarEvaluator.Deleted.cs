using System.Runtime.CompilerServices;
using EntityForge.Query.Values;

namespace EntityForge.Db.Graph;

internal static partial class GraphScalarEvaluator
{
    // Tombstones belong only to newly copied statement-local values. Weak keys
    // neither retain query rows nor change the shared transport value contracts.
    private static readonly ConditionalWeakTable<QueryValue, object> DeletedValues = new();

    internal static QueryValue DeletedCopy(QueryValue value)
    {
        QueryValue copy = value switch
        {
            NodeValue node => node with { },
            EdgeValue edge => edge with { },
            _ => throw new ArgumentException("Only graph elements can be deleted.")
        };
        DeletedValues.Add(copy, new object());
        return copy;
    }

    internal static void RequireLiveEntity(QueryValue value)
    {
        if (DeletedValues.TryGetValue(value, out _))
        {
            throw GqlDataError("The graph element was deleted by this update; its properties, labels and relationship type are unavailable.", "22G11");
        }
    }
}
