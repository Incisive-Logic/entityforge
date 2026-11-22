using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static IEnumerable<GQLParser.ElementPatternContext> RepeatedAlternativeElements(GQLParser.PathTermContext term) =>
        term.pathFactor().OfType<GQLParser.PfPathPrimaryContext>()
            .Select(factor => factor.pathPrimary()).OfType<GQLParser.PpElementPatternContext>()
            .Select(primary => primary.elementPattern());

    private static IEnumerable<GQLParser.ElementPatternFillerContext> RepeatedAlternativeFillers(GQLParser.PathTermContext term) =>
        RepeatedAlternativeElements(term).Select(element => Descendants<GQLParser.ElementPatternFillerContext>(element).First());

    private static string? NormalizeRepeatedLabelAlternatives(IParseTree root, string source)
    {
        foreach (var quantified in Descendants<GQLParser.PfQuantifiedPathPrimaryContext>(root))
        {
            if (quantified.pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary ||
                primary.parenthesizedPathPatternExpression().pathPatternExpression() is not GQLParser.PpePatternUnionContext union)
            {
                continue;
            }
            var terms = union.pathTerm();
            bool FixedChain(GQLParser.PathTermContext term)
            {
                var factors = term.pathFactor();
                return factors.Length >= 3 && factors.Length % 2 == 1 &&
                    factors.Select((factor, index) => factor is GQLParser.PfPathPrimaryContext plain &&
                        plain.pathPrimary() is GQLParser.PpElementPatternContext element &&
                        (index % 2 == 0 ? element.elementPattern().nodePattern() is not null : element.elementPattern().edgePattern() is not null)).All(valid => valid);
            }
            if (terms.Length < 2 || !terms.All(FixedChain))
            {
                continue;
            }
            if (terms.Any(term => Descendants<GQLParser.ElementPatternPredicateContext>(term).Any()))
            {
                if (NormalizeCorrelatedRepeatedLabels(union, source) is { } constrained)
                {
                    return constrained;
                }
                continue;
            }
            if (NormalizeRepeatedDirectionUnion(union, source) is { } directed)
            {
                return directed;
            }
            var branchLabels = terms.Select(term => RepeatedAlternativeFillers(term)
                .Select(filler => filler.isLabelExpression()?.labelExpression()).OfType<GQLParser.LabelExpressionContext>().ToArray()).ToArray();
            if (branchLabels.Any(labels => labels.Length != branchLabels[0].Length))
            {
                if (NormalizeCorrelatedRepeatedLabels(union, source) is { } correlated)
                {
                    return correlated;
                }
                continue;
            }
            GQLParser.LabelExpressionContext[]? labels = null;
            for (int slot = 0; slot < branchLabels[0].Length; slot++)
            {
                string Shape(IParseTree node, IParseTree label) => ReferenceEquals(node, label) ? "<label-alternative>" :
                    node.ChildCount == 0 ? node.GetText() : string.Concat(Enumerable.Range(0, node.ChildCount).Select(i => Shape(node.GetChild(i), label)));
                if (terms.Select((term, index) => Shape(term, branchLabels[index][slot])).Distinct(StringComparer.Ordinal).Count() == 1)
                {
                    labels = branchLabels.Select(items => items[slot]).ToArray();
                    break;
                }
            }
            if (labels is null)
            {
                if (NormalizeCorrelatedRepeatedLabels(union, source) is { } correlated)
                {
                    return correlated;
                }
                continue;
            }
            // Identical fixed chains differing at one label position identify
            // the same path. Union becomes a Boolean label disjunction, which
            // visits that path once even when several labels match. Bag
            // alternation must not use this lowering because it retains copies.
            // Element predicates are excluded: even identical text could have
            // nondeterministic effects that differ between union branches.
            var branch = union.pathTerm()[0];
            string replacement = source[branch.Start.StartIndex..labels[0].Start.StartIndex] + "(" +
                string.Join("|", labels.Select(label => "(" + source[label.Start.StartIndex..(label.Stop.StopIndex + 1)] + ")")) + ")" +
                source[(labels[0].Stop.StopIndex + 1)..(branch.Stop.StopIndex + 1)];
            return source.Remove(union.Start.StartIndex, union.Stop.StopIndex - union.Start.StartIndex + 1)
                .Insert(union.Start.StartIndex, replacement);
        }
        return null;
    }

    private static string? NormalizeRepeatedDirectionUnion(GQLParser.PpePatternUnionContext union, string source)
    {
        var terms = union.pathTerm();
        var edges = terms.Select(term => RepeatedAlternativeElements(term).Select(element => element.edgePattern()).OfType<GQLParser.EdgePatternContext>().ToArray()).ToArray();
        if (edges.Any(branch => branch.Length != edges[0].Length))
        {
            return null;
        }
        for (int slot = 0; slot < edges[0].Length; slot++)
        {
            var selected = edges.Select(branch => branch[slot]).ToArray();
            var full = selected.Select(edge => edge.fullEdgePattern()).ToArray();
            if (full.Any(edge => edge is null || edge.fullEdgeUndirected() is not null))
            {
                continue;
            }
            // Only one direction position may differ. Keep bindings, labels and
            // every other factor identical; independent unions at two positions
            // would introduce combinations absent from the original branches.
            string Shape(IParseTree node, GQLParser.EdgePatternContext edge) => ReferenceEquals(node, edge)
                ? "<direction>" + Descendants<GQLParser.ElementPatternFillerContext>(edge).Single().GetText()
                : node.ChildCount == 0 ? node.GetText()
                : string.Concat(Enumerable.Range(0, node.ChildCount).Select(i => Shape(node.GetChild(i), edge)));
            if (terms.Select((term, index) => Shape(term, selected[index])).Distinct(StringComparer.Ordinal).Count() != 1)
            {
                continue;
            }
            int directions = full.Aggregate(0, (mask, edge) => mask |
                (edge.fullEdgePointingLeft() is not null ? 1 :
                 edge.fullEdgePointingRight() is not null ? 2 :
                 edge.fullEdgeUndirected() is not null ? 4 :
                 edge.fullEdgeLeftOrRight() is not null ? 3 :
                 edge.fullEdgeLeftOrUndirected() is not null ? 5 :
                 edge.fullEdgeUndirectedOrRight() is not null ? 6 : 7));
            var (opening, closing) = directions switch
            {
                1 => ("<-[", "]-"),
                2 => ("-[", "]->"),
                3 => ("<-[", "]->"),
                4 => ("~[", "]~"),
                5 => ("<~[", "]~"),
                6 => ("~[", "]~>"),
                _ => ("-[", "]-")
            };
            var first = terms[0];
            var filler = Descendants<GQLParser.ElementPatternFillerContext>(selected[0]).Single();
            string replacement = source[first.Start.StartIndex..selected[0].Start.StartIndex] + opening +
                source[filler.Start.StartIndex..(filler.Stop.StopIndex + 1)] + closing +
                source[(selected[0].Stop.StopIndex + 1)..(first.Stop.StopIndex + 1)];
            return source.Remove(union.Start.StartIndex, union.Stop.StopIndex - union.Start.StartIndex + 1)
                .Insert(union.Start.StartIndex, replacement);
        }
        return null;
    }

    private static string? NormalizeCorrelatedRepeatedLabels(GQLParser.PpePatternUnionContext union, string source)
    {
        string Shape(IParseTree node) => node is GQLParser.IsLabelExpressionContext or GQLParser.ElementPatternPredicateContext ? "" :
            node is GQLParser.EdgePatternContext edge ? "<edge>" + Shape(Descendants<GQLParser.ElementPatternFillerContext>(edge).First()) :
            node.ChildCount == 0 ? node.GetText() : string.Concat(Enumerable.Range(0, node.ChildCount).Select(i => Shape(node.GetChild(i))));
        var terms = union.pathTerm();
        if (terms.SelectMany(RepeatedAlternativeElements).Select(element => element.edgePattern()).OfType<GQLParser.EdgePatternContext>().Any(edge =>
            edge.fullEdgePattern() is not { } full || full.fullEdgeUndirected() is not null))
        {
            return null;
        }
        if (terms.Select(Shape).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            return null;
        }
        var allEdges = terms.Select(term => RepeatedAlternativeElements(term).Select(element => element.edgePattern()).OfType<GQLParser.EdgePatternContext>().ToArray()).ToArray();
        var allFillers = terms.Select(term => RepeatedAlternativeFillers(term).ToArray()).ToArray();
        int Direction(GQLParser.EdgePatternContext edge)
        {
            var full = edge.fullEdgePattern();
            return full.fullEdgePointingRight() is not null || full.fullEdgeUndirectedOrRight() is not null ? 1 :
                full.fullEdgePointingLeft() is not null || full.fullEdgeLeftOrUndirected() is not null ? 2 : 3;
        }
        var candidateDirections = Enumerable.Range(0, allEdges[0].Length)
            .Select(index => allEdges.Aggregate(0, (mask, edges) => mask | Direction(edges[index]))).ToArray();
        var branches = new List<string>();
        foreach (var term in terms)
        {
            var conditions = new List<string>();
            var fillers = RepeatedAlternativeFillers(term).ToArray();
            var termEdges = RepeatedAlternativeElements(term).Select(element => element.edgePattern()).OfType<GQLParser.EdgePatternContext>().ToArray();
            for (int index = 0; index < termEdges.Length; index++)
            {
                var full = termEdges[index].fullEdgePattern();
                bool forward = full.fullEdgePointingRight() is not null || full.fullEdgeUndirectedOrRight() is not null;
                bool reverse = full.fullEdgePointingLeft() is not null || full.fullEdgeLeftOrUndirected() is not null;
                if ((!forward && !reverse) || candidateDirections[index] != 3)
                {
                    continue;
                }
                if (fillers[index * 2].elementVariableDeclaration() is not { } node ||
                    fillers[index * 2 + 1].elementVariableDeclaration() is not { } relationship)
                {
                    return null;
                }
                conditions.Add(node.GetText() + (forward ? " IS SOURCE OF " : " IS DESTINATION OF ") + relationship.GetText());
            }
            foreach (var filler in RepeatedAlternativeFillers(term))
            {
                if (filler.isLabelExpression() is null && filler.elementPatternPredicate() is null)
                {
                    continue;
                }
                if (filler.elementVariableDeclaration() is not { } variable)
                {
                    return null;
                }
                if (filler.isLabelExpression() is { } label)
                {
                    conditions.Add(variable.GetText() + " IS LABELED (" + label.labelExpression().GetText() + ")");
                }
                if (filler.elementPatternPredicate()?.elementPatternWhereClause() is { } where)
                {
                    var condition = where.searchCondition();
                    conditions.Add("(" + source[condition.Start.StartIndex..(condition.Stop.StopIndex + 1)] + ")");
                }
                var pairs = filler.elementPatternPredicate()?.elementPropertySpecification()?.propertyKeyValuePairList().propertyKeyValuePair() ?? [];
                if (pairs.Select(pair => GraphGqlIdentifiers.DecodeIdentifier(pair.propertyName().GetText()))
                    .Distinct(StringComparer.Ordinal).Count() != pairs.Length)
                {
                    throw new ArgumentException("A repeated element property specification cannot repeat a property name.");
                }
                foreach (var pair in pairs)
                {
                    conditions.Add("(" + variable.GetText() + "." + pair.propertyName().GetText() + " = (" +
                        source[pair.valueExpression().Start.StartIndex..(pair.valueExpression().Stop.StopIndex + 1)] + "))");
                }
            }
            branches.Add(conditions.Count == 0 ? "TRUE" : "(" + string.Join(" AND ", conditions) + ")");
        }
        var first = terms[0];
        var parts = new List<string>();
        var factors = first.pathFactor();
        for (int index = 0; index < factors.Length; index++)
        {
            var factor = factors[index];
            var filler = Descendants<GQLParser.ElementPatternFillerContext>(factor).First();
            string text = filler.elementVariableDeclaration()?.GetText() ?? "";
            // Retain necessary per-position constraints during traversal. The
            // full branch predicate below still enforces their correlation.
            // Without these constraints, unrelated labels and reverse paths
            // can exhaust work budgets before qualifying paths are reached.
            var labels = allFillers.Select(items => items[index].isLabelExpression()?.labelExpression()).ToArray();
            if (labels.All(item => item is not null))
            {
                text += " IS (" + string.Join("|", labels.Select(item => "(" + item!.GetText() + ")").Distinct(StringComparer.Ordinal)) + ")";
            }
            if (index % 2 == 0)
            {
                parts.Add("(" + text + ")");
            }
            else
            {
                int direction = candidateDirections[index / 2];
                parts.Add((direction == 2 ? "<-[" : "-[") + text + (direction == 1 ? "]->" : "]-"));
            }
        }
        string pattern = string.Concat(parts);
        // Keep each branch's labels correlated. One Boolean prefilter gives
        // set-union multiplicity even when more than one branch is satisfied.
        string replacement = "(" + pattern + " WHERE " + string.Join(" OR ", branches) + ")";
        if ((long)source.Length - (union.Stop.StopIndex - union.Start.StartIndex + 1) + replacement.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("Repeated label alternatives exceed the query character limit.");
        }
        return source.Remove(union.Start.StartIndex, union.Stop.StopIndex - union.Start.StartIndex + 1)
            .Insert(union.Start.StartIndex, replacement);
    }
}
