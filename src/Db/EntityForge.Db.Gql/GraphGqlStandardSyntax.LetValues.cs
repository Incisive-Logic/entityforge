using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeUntypedLetDeclarations(IParseTree root, string source)
    {
        var declarations = Descendants<GQLParser.ValueVariableDefinitionContext>(root)
            .Where(definition => definition.Parent is GQLParser.LetVariableDefinitionContext &&
                definition.optTypedValueInitializer().valueType() is null)
            .OrderByDescending(definition => definition.Start.StartIndex).ToArray();
        if (declarations.Length == 0)
        {
            return null;
        }
        foreach (var declaration in declarations)
        {
            source = source.Remove(declaration.Start.StartIndex,
                declaration.bindingVariable().Start.StartIndex - declaration.Start.StartIndex);
        }
        return source;
    }

    private static string? NormalizeLetValueExpression(IParseTree root, string source)
    {
        var expression = Descendants<GQLParser.LetValueExpressionContext>(root)
            .OrderByDescending(item => item.Start.StartIndex).FirstOrDefault();
        if (expression is null)
        {
            return null;
        }
        string alias = "__letValueResult";
        while (source.Contains(alias, StringComparison.Ordinal))
        {
            alias += "_";
        }
        var definitions = expression.letVariableDefinitionList();
        var value = expression.valueExpression();
        // A scalar query provides a local working row and exactly one result.
        // Reuse LET assignment/type checks and VALUE's lexical import handling;
        // local bindings must never be exported into the enclosing working row.
        string replacement = "VALUE { LET " + source[definitions.Start.StartIndex..(definitions.Stop.StopIndex + 1)] +
            " RETURN " + source[value.Start.StartIndex..(value.Stop.StopIndex + 1)] + " AS " + alias + " }";
        if ((long)source.Length - (expression.Stop.StopIndex - expression.Start.StartIndex + 1) + replacement.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("LET value expression lowering exceeds the query character limit.");
        }
        return source.Remove(expression.Start.StartIndex, expression.Stop.StopIndex - expression.Start.StartIndex + 1)
            .Insert(expression.Start.StartIndex, replacement);
    }
}
