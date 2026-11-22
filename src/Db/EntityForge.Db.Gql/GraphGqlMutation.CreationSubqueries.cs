namespace EntityForge.Db.Gql;

internal static partial class GraphGqlMutationCompiler
{
    private static (string Action, List<string> Projections, HashSet<string> Aliases) LiftCreationSubqueries(
        string action, IReadOnlySet<string> scope, string source)
    {
        var projections = new List<string>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var edits = new List<(int Start, int Length, string Text)>();
        string masked = GraphGqlQueryCompiler.MaskQuotedStrings(action);
        for (int start = 0; start < masked.Length; start++)
        {
            if (masked[start] != '{')
            {
                continue;
            }
            int end = start + 1, depth = 1;
            for (; end < masked.Length && depth > 0; end++)
            {
                if (masked[end] == '{')
                {
                    depth++;
                }
                else if (masked[end] == '}')
                {
                    depth--;
                }
            }
            if (depth != 0)
            {
                throw new ArgumentException("Unbalanced creation property map.");
            }
            var properties = new List<string>();
            string body = action[(start + 1)..(end - 1)];
            foreach (string item in String.IsNullOrWhiteSpace(body) ? [] : SplitTopLevel(body))
            {
                var pair = ParsePropertyPair(item);
                if (!HasMutationValueSubquery(pair.Expression))
                {
                    properties.Add(item);
                    continue;
                }
                string alias = "__creationValue" + projections.Count;
                while (scope.Contains(alias) || aliases.Contains(alias) || source.Contains(alias, StringComparison.Ordinal))
                {
                    alias += "_";
                }
                aliases.Add(alias);
                projections.Add(pair.Expression + " AS " + alias);
                // Retain the original property spelling, including delimiters.
                int colon = GraphGqlQueryCompiler.MaskQuotedStrings(item).IndexOf(':');
                properties.Add(item[..(colon + 1)] + alias);
            }
            edits.Add((start + 1, end - start - 2, string.Join(',', properties)));
            start = end - 1;
        }
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            action = action.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }
        return (action, projections, aliases);
    }
}
