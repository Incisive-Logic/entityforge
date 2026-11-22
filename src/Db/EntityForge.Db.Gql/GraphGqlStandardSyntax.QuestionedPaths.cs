using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeQuestionedPaths(IParseTree root, string source)
    {
        foreach (var questioned in Descendants<GQLParser.PfQuestionedPathPrimaryContext>(root))
        {
            var primary = questioned.pathPrimary();
            bool supportedPrimary = primary is GQLParser.PpParenthesizedPathPatternExpressionContext ||
                primary is GQLParser.PpElementPatternContext element && element.elementPattern().edgePattern() is not null;
            if (!supportedPrimary ||
                PatternSyntaxDescendants<GQLParser.PfQuantifiedPathPrimaryContext>(primary).Any(quantified =>
                    PatternDeclarations(quantified).Any() ||
                    PatternSyntaxDescendants<GQLParser.SubpathVariableDeclarationContext>(quantified).Any()) ||
                PatternSyntaxDescendants<GQLParser.PfQuestionedPathPrimaryContext>(primary).Any())
            {
                continue;
            }
            // A questioned positive-length body contributes either its ordinary
            // singleton bindings or an empty path with absent bindings. It must
            // not use {0,1}, which changes singleton exposure to group exposure.
            // Bag alternation preserves multiplicity inside the body; the empty
            // branch cannot overlap a body of positive minimum length.
            // Anonymous repetitions add no group-valued columns to reconcile
            // with the empty branch. Named groups still require that support.
            string body = source[primary.Start.StartIndex..(primary.Stop.StopIndex + 1)];
            string replacement = "(" + body + " |+| ())";
            if ((long)source.Length - (questioned.Stop.StopIndex - questioned.Start.StartIndex + 1) +
                replacement.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
            {
                throw InvalidSyntax("Expanded questioned paths exceed the query character limit.");
            }
            return source.Remove(questioned.Start.StartIndex,
                questioned.Stop.StopIndex - questioned.Start.StartIndex + 1).Insert(questioned.Start.StartIndex, replacement);
        }
        return null;
    }

    // A nested query owns its path syntax. Its repetitions must not change
    // exposure or eligibility of the enclosing path being lowered.
    private static IEnumerable<T> PatternSyntaxDescendants<T>(IParseTree root) where T : class, IParseTree
    {
        var pending = new Stack<IParseTree>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is GQLParser.ExistsPredicateContext or GQLParser.NestedQuerySpecificationContext or
                GQLParser.NestedProcedureSpecificationContext)
            {
                continue;
            }
            if (node is T matching)
            {
                yield return matching;
            }
            for (int index = node.ChildCount - 1; index >= 0; index--)
            {
                pending.Push(node.GetChild(index));
            }
        }
    }
}
