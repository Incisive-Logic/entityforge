using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeDirectedAlternatives(IParseTree root, string source)
    {
        var edits = new List<(int Start, int Length, string Text)>();
        void Replace(IToken token, string text) => edits.Add((token.StartIndex, token.StopIndex - token.StartIndex + 1, text));
        // This store admits only directed edges. A union of one directed
        // orientation and undirected edges therefore has that orientation's
        // matches. Normalize before repeated-pattern lowering so each iteration
        // uses the same direction, including reverse traversal.
        foreach (var edge in Descendants<GQLParser.FullEdgeLeftOrUndirectedContext>(root))
        {
            Replace(edge.Start, "<-[");
            Replace(edge.Stop, "]-");
        }
        foreach (var edge in Descendants<GQLParser.FullEdgeUndirectedOrRightContext>(root))
        {
            Replace(edge.Start, "-[");
            Replace(edge.Stop, "]->");
        }
        foreach (var edge in Descendants<GQLParser.AbbreviatedEdgePatternContext>(root))
        {
            if (edge.GetText() is "<~" or "~>")
            {
                Replace(edge.Start, edge.GetText() == "<~" ? "<-" : "->");
            }
        }
        if (edits.Count == 0)
        {
            return null;
        }
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            source = source.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }
        return source;
    }
}
