using EntityForge.Db.Query.Syntax.Gql;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    internal static string BindSessionUser(string source, string user)
    {
        if (String.IsNullOrEmpty(source) || !source.Contains("SESSION_USER", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }
        ArgumentException.ThrowIfNullOrEmpty(user);
        var root = GqlSyntaxParser.Parse(source, out _);
        ValidateProjectionAliases(root);
        var expressions = Descendants<GQLParser.GeneralValueSpecificationContext>(root)
            .Where(expression => expression.SESSION_USER() is not null).ToArray();
        if (expressions.Length == 0)
        {
            return source;
        }
        var edits = expressions.Select(expression => (Start: expression.Start.StartIndex,
            Length: expression.Stop.StopIndex - expression.Start.StartIndex + 1, Text: JsonSerializer.Serialize(user))).ToList();
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            source = source.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }
        return source;
    }
}
