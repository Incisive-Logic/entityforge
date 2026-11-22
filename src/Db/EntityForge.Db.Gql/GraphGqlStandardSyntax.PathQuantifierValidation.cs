using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static void ValidatePathQuantifiers(IParseTree root)
    {
        foreach (var factor in Descendants<GQLParser.PathFactorContext>(root))
        {
            GQLParser.PathPrimaryContext? primary = factor switch
            {
                GQLParser.PfQuantifiedPathPrimaryContext quantified => quantified.pathPrimary(),
                GQLParser.PfQuestionedPathPrimaryContext questioned => questioned.pathPrimary(),
                _ => null
            };
            if (primary is null)
            {
                continue;
            }
            if (!Positive(primary))
            {
                throw InvalidSyntax("A quantified or questioned path body must have positive minimum path length.");
            }
            if (factor is GQLParser.PfQuantifiedPathPrimaryContext)
            {
                for (IParseTree? parent = factor.Parent; parent is not null && parent is not GQLParser.GraphPatternContext; parent = parent.Parent)
                {
                    if (parent is GQLParser.PfQuantifiedPathPrimaryContext)
                    {
                        throw InvalidSyntax("Path quantifiers cannot be nested at the same graph-pattern depth.");
                    }
                }
            }
        }

        static bool Positive(IParseTree node) => node switch
        {
            GQLParser.PpElementPatternContext element => element.elementPattern().edgePattern() is not null,
            GQLParser.PpParenthesizedPathPatternExpressionContext grouped => Positive(grouped.parenthesizedPathPatternExpression().pathPatternExpression()),
            GQLParser.PpePathTermContext term => Positive(term.pathTerm()),
            GQLParser.PathTermContext term => term.pathFactor().Any(Positive),
            GQLParser.PpePatternUnionContext union => union.pathTerm().All(Positive),
            GQLParser.PpeMultisetAlternationContext union => union.pathTerm().All(Positive),
            GQLParser.PfPathPrimaryContext plain => Positive(plain.pathPrimary()),
            GQLParser.PfQuestionedPathPrimaryContext => false,
            GQLParser.PfQuantifiedPathPrimaryContext quantified => Positive(quantified.pathPrimary()) &&
                PositiveBound(quantified.graphPatternQuantifier()),
            // Simplified edge-pattern syntax is validated by its own lowering.
            _ => true
        };

        static bool PositiveBound(GQLParser.GraphPatternQuantifierContext quantifier)
        {
            if (quantifier.PLUS_SIGN() is not null)
            {
                return true;
            }
            string? bound = quantifier.fixedQuantifier()?.unsignedInteger().GetText() ??
                quantifier.generalQuantifier()?.lowerBound()?.GetText();
            return bound is not null && NormalizeUnsignedInteger(bound).Any(character => character != '0');
        }
    }
}
