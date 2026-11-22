using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string SelectPathRows(string candidates, IReadOnlyList<string> columns, string path,
        string startColumn, string endColumn, GQLParser.PathSearchPrefixContext search, string? postfilter, string prefix)
    {
        var recordColumns = columns.Contains(path, StringComparer.Ordinal) ? columns.ToArray() : columns.Append(path).ToArray();
        string pool = prefix + "Pool", item = prefix + "Item", chosen = prefix + "Chosen";
        string hops = prefix + "Hops", ties = prefix + "Ties";
        var shortest = search.shortestPathSearch();
        bool groups = shortest?.allShortestPathSearch() is not null || shortest?.countedShortestGroupSearch() is not null;
        string countText = search.anyPathSearch()?.numberOfPaths()?.GetText() ??
            shortest?.countedShortestPathSearch()?.numberOfPaths()?.GetText() ??
            shortest?.countedShortestGroupSearch()?.numberOfGroups()?.GetText() ?? "1";
        if (!Int32.TryParse(NormalizeUnsignedInteger(countText), out int take) || take < 0 || take > GraphGqlQueryCompiler.MaximumAggregateInputRows)
        {
            throw new ArgumentException("Repeated-group path selection requires a literal count between 0 and 10,000.");
        }
        string length = "PATH_LENGTH(" + item + "." + path + ")";
        string selection = groups
            ? "FOR " + item + " IN " + pool + " RETURN " + length + " AS " + hops + ",COLLECT_LIST(" + item + ") AS " + ties +
                " GROUP BY " + hops + " ORDER BY " + hops + " LIMIT " + take + " NEXT FOR " + chosen + " IN " + ties + " RETURN " + chosen
            : "FOR " + item + " IN " + pool + " RETURN " + item + " AS " + chosen +
                (shortest is not null ? " ORDER BY " + length : "") + " LIMIT " + take;
        return "CALL { " + candidates + " RETURN " + startColumn + "," + endColumn + ",COLLECT_LIST(RECORD {" +
            String.Join(',', recordColumns.Select(column => column + ":" + column)) + "}) AS " + pool +
            " GROUP BY " + startColumn + "," + endColumn + " NEXT CALL (" + pool + ") { " + selection + " } LET " +
            String.Join(',', columns.Select(column => column + "=" + chosen + "." + column)) +
            (postfilter is null ? "" : " FILTER " + postfilter) +
            " RETURN " + String.Join(',', columns) + " }";
    }
}
