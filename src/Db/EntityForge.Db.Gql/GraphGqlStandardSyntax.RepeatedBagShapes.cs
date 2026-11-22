using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandRepeatedBagShapes(GQLParser.SimpleMatchStatementContext statement,
        string source, int depth, IReadOnlySet<string>? initialScope)
    {
        foreach (var quantified in Descendants<GQLParser.PfQuantifiedPathPrimaryContext>(statement))
        {
            if (quantified.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary)
            {
                continue;
            }
            var group = primary.parenthesizedPathPatternExpression();
            if (group.pathPatternExpression() is not GQLParser.PpeMultisetAlternationContext bag)
            {
                continue;
            }
            var terms = bag.pathTerm();
            // Same-shape alternatives already have a traversal implementation;
            // keep that route, including its ordered group bindings and budgets.
            if (CompatibleRepeatedBagTerms(terms) && group.pathModePrefix() is null &&
                !terms.Any(term => Descendants<GQLParser.ParenthesizedPathPatternExpressionContext>(term).Any()) &&
                !(quantified.Parent is GQLParser.PathTermContext outerTerm &&
                    (UsesRepeatedBagEdgeExteriors(outerTerm) || outerTerm.pathFactor().Any(factor =>
                        factor is GQLParser.PfPathPrimaryContext plain &&
                        plain.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext))))
            {
                continue;
            }
            var elements = new List<GQLParser.ElementPatternContext[]>();
            bool valid = true;
            foreach (var term in terms)
            {
                if (!TryCollectRepeatedBody(group, out var factors, out _, term, rootModeHandled: true, nestedModesHandled: true))
                {
                    valid = false;
                    break;
                }
                elements.Add(factors.Cast<GQLParser.PfPathPrimaryContext>()
                    .Select(factor => ((GQLParser.PpElementPatternContext)factor.pathPrimary()).elementPattern()).ToArray());
            }
            if (!valid)
            {
                continue;
            }
            if (elements.Any(items => items.Length < 3 || items.Length % 2 == 0) ||
                elements.Any(items => items.Where((item, index) => index % 2 == 0
                    ? item.nodePattern() is null : item.edgePattern() is null).Any()))
            {
                continue;
            }
            var exposed = terms.Select(term => PatternDeclarations(term).Select(variable => variable.GetText())
                .Concat(Descendants<GQLParser.SubpathVariableDeclarationContext>(term).Select(variable => variable.subpathVariable().GetText()))
                .ToHashSet(StringComparer.Ordinal)).ToArray();
            if (exposed.Skip(1).Any(names => !names.SetEquals(exposed[0])))
            {
                throw new ArgumentException("Different-length repeated alternatives require matching group binding names in every branch.");
            }
            if (TryTraverseDifferentBag(statement, quantified, terms, source, depth, initialScope) is { } traversed)
            {
                return traversed;
            }
            if (group.parenthesizedPathPatternWhereClause() is not null || group.pathModePrefix() is not null || exposed[0].Count != 0 ||
                group.subpathVariableDeclaration() is not null ||
                terms.Any(term => Descendants<GQLParser.ParenthesizedPathPatternExpressionContext>(term).Any()))
            {
                continue;
            }
            var quantifier = quantified.graphPatternQuantifier();
            string? upper = quantifier.fixedQuantifier()?.unsignedInteger().GetText() ?? quantifier.generalQuantifier()?.upperBound()?.GetText();
            if (upper is null)
            {
                continue;
            }
            string lower = quantifier.fixedQuantifier()?.unsignedInteger().GetText() ?? quantifier.generalQuantifier()?.lowerBound()?.GetText() ?? "0";
            if (!int.TryParse(NormalizeUnsignedInteger(lower), out int minimum) ||
                !int.TryParse(NormalizeUnsignedInteger(upper), out int maximum) || minimum > maximum || maximum > 16)
            {
                throw new ArgumentException("Different-shape repeated alternatives exceed the bounded expansion limit.");
            }
            string Text(ParserRuleContext node) => source[node.Start.StartIndex..(node.Stop.StopIndex + 1)];
            var choices = terms.Select(Text).ToArray();
            var branches = new List<string>();
            var previous = new List<string> { "" };
            for (int count = 0; count <= maximum; count++)
            {
                if (count >= minimum)
                {
                    branches.AddRange(previous.Select(value => value.Length == 0 ? "()" : value));
                }
                if (count == maximum)
                {
                    break;
                }
                if ((long)previous.Count * choices.Length + branches.Count > 64)
                {
                    throw new ArgumentException("Different-shape repeated alternatives exceed 64 expanded branches.");
                }
                previous = previous.SelectMany(prefix => choices.Select(choice => prefix + choice)).ToList();
                if (previous.Sum(value => (long)value.Length) > GraphGqlQueryCompiler.MaximumQueryCharacters)
                {
                    throw new ArgumentException("Repeated alternative expansion exceeds the query text budget.");
                }
            }
            string replacement = "(" + string.Join(" |+| ", branches) + ")";
            string match = Text(statement);
            match = match.Remove(quantified.Start.StartIndex - statement.Start.StartIndex,
                quantified.Stop.StopIndex - quantified.Start.StartIndex + 1)
                .Insert(quantified.Start.StartIndex - statement.Start.StartIndex, replacement);
            ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
                operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
            var scope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
            return PrepareWithElementScope(match, PreviouslyDeclaredElements(scopeContext, source, depth), depth + 1, scope);
        }
        return null;
    }
}
