using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static bool IsNextYieldReference(ParserRuleContext reference) =>
        reference is GQLParser.YieldItemNameContext &&
        reference.Parent?.Parent?.Parent?.Parent is GQLParser.NextStatementContext;

    private static string CorrelatedReferenceName(ParserRuleContext reference) => IsNextYieldReference(reference)
        ? GraphGqlIdentifiers.DecodeIdentifier(reference.GetText()) : reference.GetText();

    private static bool IsScalarProjectionAliasReference(ParserRuleContext reference)
    {
        bool resultClause = false;
        for (var ancestor = reference.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            resultClause |= ancestor is GQLParser.OrderByAndPageStatementContext or GQLParser.GroupingElementContext;
            if (ancestor is GQLParser.PrimitiveResultStatementContext result)
            {
                return resultClause && (result.returnStatement()?.returnStatementBody().returnItemList()?.returnItem() ?? [])
                    .Any(item => item.returnItemAlias() is { } alias &&
                        GraphGqlIdentifiers.DecodeIdentifier(alias.identifier().GetText()) == reference.GetText());
            }
        }
        return false;
    }

    private static void ValidateCorrelatedScalarQuery(ParserRuleContext nested,
        IReadOnlySet<string> elementNames, string? pathName = null)
    {
        // Validate before renaming pattern elements. Otherwise a local declaration
        // could retain its name while references to it acquire an element alias.
        bool DirectStatement(ParserRuleContext statement)
        {
            for (var ancestor = statement.Parent; ancestor is not null && !ReferenceEquals(ancestor, nested); ancestor = ancestor.Parent)
            {
                if (ancestor is GQLParser.NestedQuerySpecificationContext or GQLParser.NestedProcedureSpecificationContext or
                    GQLParser.MatchStatementContext or GQLParser.FilterStatementContext or GQLParser.LetStatementContext or
                    GQLParser.ForStatementContext or GQLParser.ReturnStatementContext)
                {
                    return false;
                }
            }
            return true;
        }
        var returns = Descendants<GQLParser.ReturnStatementContext>(nested).Where(DirectStatement).ToArray();
        var lets = Descendants<GQLParser.LetStatementContext>(nested).Where(DirectStatement).ToArray();
        var iterations = Descendants<GQLParser.ForStatementContext>(nested).Where(DirectStatement).ToArray();
        var prefixes = Descendants<GQLParser.FilterStatementContext>(nested).Where(DirectStatement).Cast<ParserRuleContext>()
            .Concat(lets).Concat(iterations).Concat(Descendants<GQLParser.MatchStatementContext>(nested).Where(DirectStatement))
            .Concat(Descendants<GQLParser.CallQueryStatementContext>(nested).Where(DirectStatement))
            .OrderBy(prefix => prefix.Start.StartIndex).ToArray();
        string body = nested is GQLParser.NestedQuerySpecificationContext or GQLParser.NestedProcedureSpecificationContext
            ? nested.GetText()[1..^1] : nested.GetText();
        var branches = Descendants<GQLParser.CompositeQueryPrimaryContext>(nested).Where(DirectStatement).ToArray();
        var blocks = Descendants<GQLParser.StatementBlockContext>(nested).Where(DirectStatement).ToArray();
        if (blocks.Length == 1 && blocks[0].nextStatement().Length > 0)
        {
            var block = blocks[0];
            if (body != block.GetText())
            {
                throw new ArgumentException("Correlated NEXT queries require a query statement block.");
            }
            var statements = new[] { block.statement() }.Concat(block.nextStatement().Select(next => next.statement())).ToArray();
            foreach (var statement in statements)
            {
                ValidateCorrelatedScalarQuery(statement, elementNames, pathName);
            }
            foreach (var projection in returns.Where(statement => statement.Stop.StopIndex < statements[^1].Start.StartIndex))
            {
                if ((projection.returnStatementBody().returnItemList()?.returnItem() ?? []).Any(item =>
                    item.returnItemAlias() is { } alias && elementNames.Contains(GraphGqlIdentifiers.DecodeIdentifier(alias.identifier().GetText()))))
                {
                    throw new ArgumentException("An intermediate scalar-query alias cannot replace a pattern element.");
                }
            }
        }
        else if (branches.Length > 1)
        {
            var conjunctions = Descendants<GQLParser.QueryConjunctionContext>(nested).Where(DirectStatement).ToArray();
            if (conjunctions.Length != branches.Length - 1 || conjunctions.Any(conjunction => conjunction.setOperator()?.UNION() is null) ||
                body != String.Concat(branches.Cast<ParserRuleContext>().Concat(conjunctions)
                    .OrderBy(part => part.Start.StartIndex).Select(part => part.GetText())))
            {
                throw new ArgumentException("Correlated scalar-query composition currently requires UNION branches.");
            }
            foreach (var branch in branches)
            {
                ValidateCorrelatedScalarQuery(branch, elementNames, pathName);
            }
        }
        else if (returns.Length != 1 ||
            returns[0].Parent is not GQLParser.PrimitiveResultStatementContext result ||
            body != String.Concat(prefixes.Select(prefix => prefix.GetText())) + result.GetText())
        {
            throw new ArgumentException("Correlated scalar queries in pattern predicates require one RETURN projection after a linear MATCH, FILTER, LET, FOR or read CALL sequence.");
        }
        var locals = lets.SelectMany(statement => statement.letVariableDefinitionList().letVariableDefinition())
            .Select(definition => (definition.bindingVariable() ?? definition.valueVariableDefinition().bindingVariable()).GetText())
            .Concat(iterations.Select(statement => statement.forItem().forItemAlias().bindingVariable().GetText()))
            .Concat(iterations.Where(statement => statement.forOrdinalityOrOffset() is not null)
                .Select(statement => statement.forOrdinalityOrOffset().bindingVariable().GetText()))
            .Concat(Descendants<GQLParser.YieldItemAliasContext>(nested).Where(DirectStatement)
                .Select(alias => alias.bindingVariable().GetText()));
        foreach (string local in locals)
        {
            if (elementNames.Contains(local) || local == pathName)
            {
                throw new ArgumentException("A local scalar-query binding cannot replace a pattern element or its enclosing path.");
            }
        }
        if (nested is GQLParser.NestedProcedureSpecificationContext &&
            returns.SelectMany(statement => statement.returnStatementBody().returnItemList()?.returnItem() ?? []).Any(item =>
                item.returnItemAlias() is { } alias &&
                (elementNames.Contains(GraphGqlIdentifiers.DecodeIdentifier(alias.identifier().GetText())) ||
                 GraphGqlIdentifiers.DecodeIdentifier(alias.identifier().GetText()) == pathName)))
        {
            throw new ArgumentException("A scalar-query CALL output cannot replace a pattern element or its enclosing path.");
        }
    }
}
