using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandPropertyExpressions(GQLParser.SimpleMatchStatementContext statement, string source, int depth)
    {
        var maps = Descendants<GQLParser.ElementPropertySpecificationContext>(statement)
            .Where(map => map.Parent?.Parent is GQLParser.ElementPatternFillerContext filler &&
                filler.elementVariableDeclaration() is not null &&
                (Descendants<GQLParser.BindingVariableReferenceContext>(map).Any() ||
                    map.propertyKeyValuePairList().propertyKeyValuePair().Any(pair => pair.propertyName().GetText().StartsWith('"'))) &&
                !InsideAnotherMap(map))
            .OrderByDescending(map => map.Start.StartIndex).ToArray();
        if (maps.Length == 0)
        {
            return null;
        }
        string match = source[statement.Start.StartIndex..(statement.Stop.StopIndex + 1)];
        foreach (var map in maps)
        {
            var filler = (GQLParser.ElementPatternFillerContext)map.Parent.Parent;
            string binding = filler.elementVariableDeclaration().GetText();
            string predicate = String.Join(" AND ", map.propertyKeyValuePairList().propertyKeyValuePair().Select(pair =>
                binding + "." + pair.propertyName().GetText() + " = (" +
                source[pair.valueExpression().Start.StartIndex..(pair.valueExpression().Stop.StopIndex + 1)] + ")"));
            int offset = map.Start.StartIndex - statement.Start.StartIndex;
            match = match.Remove(offset, map.Stop.StopIndex - map.Start.StartIndex + 1).Insert(offset, " WHERE " + predicate + " ");
        }
        // Re-enter the normal predicate lowering: repetition checks each occurrence,
        // and selective paths filter eligible candidates before choosing a path.
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var scope = CompositeScopeBefore(scopeContext, source, depth, null);
        var bound = PreviouslyDeclaredElements(statement, source, depth)
            .Concat(PatternDeclarations(statement).Select(variable => variable.GetText()).Where(scope.Contains));
        return PrepareWithElementScope(match, bound, depth + 1, scope);

        static bool InsideAnotherMap(GQLParser.ElementPropertySpecificationContext map)
        {
            for (var ancestor = map.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor is GQLParser.ElementPropertySpecificationContext)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
