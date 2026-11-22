using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? NormalizeFixedExteriorPathAlternatives(IParseTree root, string source)
    {
        // Normalize a concatenation inside its own parenthesized expression
        // too. Keeping the surrounding wrapper preserves its predicate/mode;
        // only inspecting complete paths leaves nested questioned alternatives
        // stranded behind a wrapper that cannot be flattened safely.
        foreach (var expression in Descendants<GQLParser.PpePathTermContext>(root))
        {
            var factors = expression.pathTerm().pathFactor();
            bool IsElement(GQLParser.PathFactorContext factor) => factor is GQLParser.PfPathPrimaryContext plain &&
                plain.pathPrimary() is GQLParser.PpElementPatternContext;
            var groups = factors.Where(factor => !IsElement(factor)).ToArray();
            GQLParser.PathTermContext[] Terms(GQLParser.PathPatternExpressionContext candidate) => candidate switch
            {
                GQLParser.PpePatternUnionContext union => union.pathTerm(),
                GQLParser.PpeMultisetAlternationContext multiset => multiset.pathTerm(),
                GQLParser.PpePathTermContext term => [term.pathTerm()],
                _ => []
            };
            int EdgeCount(GQLParser.PathTermContext term) => term.pathFactor().Count(factor =>
                factor is GQLParser.PfPathPrimaryContext plain &&
                plain.pathPrimary() is GQLParser.PpElementPatternContext element &&
                element.elementPattern().edgePattern() is not null);
            var alternativeFactor = groups.Where(factor =>
                factor is GQLParser.PfPathPrimaryContext plain &&
                plain.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext nested &&
                nested.parenthesizedPathPatternExpression().pathPatternExpression() is
                    GQLParser.PpePatternUnionContext or GQLParser.PpeMultisetAlternationContext)
                .OrderByDescending(factor => ((GQLParser.PpParenthesizedPathPatternExpressionContext)
                    ((GQLParser.PfPathPrimaryContext)factor).pathPrimary()).parenthesizedPathPatternExpression()
                    .pathPatternExpression() is GQLParser.PpeMultisetAlternationContext)
                .ThenByDescending(factor => Terms(((GQLParser.PpParenthesizedPathPatternExpressionContext)
                    ((GQLParser.PfPathPrimaryContext)factor).pathPrimary()).parenthesizedPathPatternExpression()
                    .pathPatternExpression()).Select(EdgeCount).Distinct().Count())
                .FirstOrDefault();
            if (alternativeFactor is not GQLParser.PfPathPrimaryContext grouped ||
                grouped.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary)
            {
                continue;
            }
            var group = primary.parenthesizedPathPatternExpression();
            if (factors.Length == 1 && group.pathModePrefix() is null)
            {
                continue;
            }
            var alternatives = Terms(group.pathPatternExpression());
            if (alternatives.Length == 0)
            {
                continue;
            }
            var declarations = alternatives.Select(term => PatternDeclarations(term)
                .Select(variable => variable.GetText()).ToHashSet(StringComparer.Ordinal)).ToArray();
            var conditional = declarations.SelectMany(names => names).Distinct(StringComparer.Ordinal)
                .Where(name => declarations.Any(names => !names.Contains(name))).ToHashSet(StringComparer.Ordinal);
            if (factors.Where(factor => !ReferenceEquals(factor, alternativeFactor))
                .SelectMany(PatternDeclarations).Any(variable => conditional.Contains(variable.GetText())))
            {
                // Distribution must not hide an illegal implicit join between
                // a conditional singleton in the group and its exterior.
                throw InvalidSyntax("A conditional path variable cannot be implicitly joined within a path concatenation.");
            }
            if (groups.Length > 1 && !groups.All(factor =>
                factor is GQLParser.PfPathPrimaryContext plain &&
                plain.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext nested &&
                FixedCompatibleGroup(nested.parenthesizedPathPatternExpression())))
            {
                continue;
            }
            bool FixedCompatibleGroup(GQLParser.ParenthesizedPathPatternExpressionContext candidate)
            {
                if (candidate.pathPatternExpression() is not GQLParser.PpePathTermContext &&
                    group.pathPatternExpression() is not GQLParser.PpeMultisetAlternationContext &&
                    candidate.pathPatternExpression().GetType() != group.pathPatternExpression().GetType())
                {
                    return false;
                }
                var terms = Terms(candidate.pathPatternExpression());
                return terms.Length > 0 && terms.All(term => term.pathFactor().All(IsElement)) &&
                    (ReferenceEquals(candidate, group) || group.pathPatternExpression() is GQLParser.PpeMultisetAlternationContext ||
                    terms.Select(EdgeCount).Distinct().Count() == 1);
            }
            // Fixed exterior elements determine unique prefix/suffix slices of
            // each complete path. Other union groups must have a fixed length
            // and the same operator; subsequent passes distribute them.
            // Expand the differing-length group first so its fixed exterior
            // determines the unique slice on which union removes duplicates.
            // Multiset alternatives retain every decomposition, so their
            // branches may have different fixed lengths without deduplication.
            // Expand multiset groups before union groups: each bag branch
            // gets its own union evaluation, preserving bag multiplicity.
            // Distributing over this alternative group
            // therefore preserves its set/bag behavior without losing an
            // independent variable-length exterior decomposition.
            string prefix = source[expression.Start.StartIndex..group.Start.StartIndex];
            string suffix = source[(group.Stop.StopIndex + 1)..(expression.Stop.StopIndex + 1)];
            string operation = group.pathPatternExpression() is GQLParser.PpePatternUnionContext ? " | " : " |+| ";
            var branches = new List<string>();
            long size = source.Length - (expression.Stop.StopIndex - expression.Start.StartIndex + 1);
            foreach (var alternative in alternatives)
            {
                int length = alternative.Stop.StopIndex - alternative.Start.StartIndex + 1;
                string branch = source.Substring(alternative.Start.StartIndex, length);
                if (group.subpathVariableDeclaration() is not null || group.pathModePrefix() is not null)
                {
                    // Bind and constrain this slice, not the fixed exterior.
                    // Every distributed branch retains the named group's exact
                    // endpoints before whole-path union or selection runs.
                    string binding = group.subpathVariableDeclaration() is { } declaration
                        ? source[declaration.Start.StartIndex..(declaration.Stop.StopIndex + 1)] + " " : "";
                    string mode = group.pathModePrefix() is { } localMode
                        ? source[localMode.Start.StartIndex..(localMode.Stop.StopIndex + 1)] + " " : "";
                    branch = "(" + binding + mode + branch + ")";
                }
                size += (long)prefix.Length + suffix.Length + branch.Length + operation.Length + 2;
                if (size > GraphGqlQueryCompiler.MaximumQueryCharacters)
                {
                    throw new ArgumentException("Expanded path alternatives exceed the query character limit.");
                }
                branches.Add(prefix + " " + branch + " " + suffix);
            }
            string replacement = string.Join(operation, branches);
            if (group.parenthesizedPathPatternWhereClause() is { } predicate)
            {
                // Filter combined alternatives once, after duplicate handling.
                // The fixed exterior gives each complete path a unique slice;
                // keep this prefilter inside whole-path selection as well.
                replacement = "(" + replacement + " " +
                    source[predicate.Start.StartIndex..(predicate.Stop.StopIndex + 1)] + ")";
            }
            if ((long)source.Length - (expression.Stop.StopIndex - expression.Start.StartIndex + 1) + replacement.Length >
                GraphGqlQueryCompiler.MaximumQueryCharacters)
            {
                throw new ArgumentException("Expanded path alternatives exceed the query character limit.");
            }
            return source.Remove(expression.Start.StartIndex, expression.Stop.StopIndex - expression.Start.StartIndex + 1)
                .Insert(expression.Start.StartIndex, replacement);
        }
        return null;
    }
}
