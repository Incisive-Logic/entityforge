using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;
using System.Text;
using System.Text.Json;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryTraverseDifferentBag(GQLParser.SimpleMatchStatementContext statement,
        GQLParser.PfQuantifiedPathPrimaryContext quantified, GQLParser.PathTermContext[] terms,
        string source, int depth, IReadOnlySet<string>? initialScope)
    {
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null ||
            graph.pathPatternList().pathPattern() is not [var path] ||
            path.pathPatternExpression() is not GQLParser.PpePathTermContext outer ||
            path.pathVariableDeclaration() is not { } declaration)
        {
            return null;
        }
        string Text(ParserRuleContext node) => source[node.Start.StartIndex..(node.Stop.StopIndex + 1)];
        var exteriorGroupPredicates = new List<string>();
        var flattened = new List<GQLParser.PathFactorContext>();
        foreach (var factor in outer.pathTerm().pathFactor())
        {
            if (factor is GQLParser.PfPathPrimaryContext plain &&
                plain.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext grouped)
            {
                var group = grouped.parenthesizedPathPatternExpression();
                if (!TryCollectRepeatedBody(group, out var fixedFactors, out var fixedGroups) ||
                    fixedGroups.Any(item => item.Group.subpathVariableDeclaration() is not null))
                {
                    return null;
                }
                // Named fixed subpaths have already been lowered to scalar
                // path constructors. Preserve the remaining predicates until
                // all candidate bindings exist, before applying a selector.
                flattened.AddRange(fixedFactors);
                exteriorGroupPredicates.AddRange(fixedGroups
                    .Select(item => item.Group.parenthesizedPathPatternWhereClause()?.searchCondition())
                    .OfType<GQLParser.SearchConditionContext>().Select(Text));
            }
            else
            {
                flattened.Add(factor);
            }
        }
        var factors = flattened.ToArray();
        int repeatedIndex = Array.IndexOf(factors, quantified);
        GQLParser.NodePatternContext? Boundary(GQLParser.PathFactorContext factor) =>
            factor is GQLParser.PfPathPrimaryContext plain && plain.pathPrimary() is GQLParser.PpElementPatternContext element
                ? element.elementPattern().nodePattern() : null;
        if (repeatedIndex < 0)
        {
            return null;
        }
        var boundaries = new List<(GQLParser.NodePatternContext Node, string? Name, bool Start)>();
        int prefixEnd = repeatedIndex, suffixStart = repeatedIndex + 1;
        if (repeatedIndex > 0 && Boundary(factors[repeatedIndex - 1]) is { } before)
        {
            boundaries.Add((before, before.elementPatternFiller().elementVariableDeclaration()?.GetText(), true));
            prefixEnd--;
        }
        if (suffixStart < factors.Length && Boundary(factors[suffixStart]) is { } after)
        {
            boundaries.Add((after, after.elementPatternFiller().elementVariableDeclaration()?.GetText(), false));
            suffixStart++;
        }
        var exterior = factors[..prefixEnd].Concat(factors[suffixStart..]).ToArray();
        GQLParser.EdgePatternContext? QuantifiedEdge(GQLParser.PathFactorContext factor) =>
            factor is GQLParser.PfQuantifiedPathPrimaryContext quantifiedEdge &&
            quantifiedEdge.pathPrimary() is GQLParser.PpElementPatternContext element
                ? element.elementPattern().edgePattern() : null;
        if (exterior.Any(factor => !(factor is GQLParser.PfPathPrimaryContext plain &&
            plain.pathPrimary() is GQLParser.PpElementPatternContext) && QuantifiedEdge(factor) is null))
        {
            return null;
        }
        var exteriorNames = exterior.SelectMany(PatternDeclarations).Select(variable => variable.GetText())
            .ToHashSet(StringComparer.Ordinal);
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var declared = PreviouslyDeclaredElements(scopeContext, source, depth).ToHashSet(StringComparer.Ordinal);
        var scope = CompositeScopeBefore(scopeContext, source, depth, initialScope);
        declared.UnionWith(scope);
        var anchorAliases = boundaries.Select(boundary => boundary.Name).OfType<string>()
            .Where(name => !declared.Contains(name) && !exteriorNames.Contains(name)).Distinct(StringComparer.Ordinal).ToArray();
        var exported = boundaries.Select(boundary => boundary.Name).OfType<string>().Concat(exteriorNames)
            .Where(name => !declared.Contains(name)).Distinct(StringComparer.Ordinal).ToArray();
        var quantifier = quantified.graphPatternQuantifier();
        string? fixedBound = quantifier.fixedQuantifier()?.unsignedInteger().GetText();
        string lower = fixedBound ?? quantifier.generalQuantifier()?.lowerBound()?.GetText() ?? (quantifier.GetText() == "+" ? "1" : "0");
        string? upper = fixedBound ?? quantifier.generalQuantifier()?.upperBound()?.GetText();
        if (!int.TryParse(NormalizeUnsignedInteger(lower), out int minimum) || minimum > 10_000)
        {
            throw new ArgumentException("Repeated-group minimum exceeds 10,000 iterations.");
        }
        var repeatedGroup = ((GQLParser.PpParenthesizedPathPatternExpressionContext)quantified.pathPrimary())
            .parenthesizedPathPatternExpression();
        var bodies = terms.Select(term =>
        {
            if (!TryCollectRepeatedBody(repeatedGroup, out var bodyFactors, out var groups, term, rootModeHandled: true, nestedModesHandled: true))
            {
                throw new ArgumentException("Repeated alternatives require fixed branch bodies with supported subpath modes.");
            }
            return (Elements: bodyFactors.Cast<GQLParser.PfPathPrimaryContext>()
                .Select(factor => ((GQLParser.PpElementPatternContext)factor.pathPrimary()).elementPattern()).ToArray(), Groups: groups);
        }).ToArray();
        var elements = bodies.Select(body => body.Elements).ToArray();
        string? Binding(GQLParser.ElementPatternContext element) => Descendants<GQLParser.ElementPatternFillerContext>(element)
            .FirstOrDefault()?.elementVariableDeclaration()?.GetText();
        string[] groupNames = elements[0].Select(Binding).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        string iterationMode = repeatedGroup.pathModePrefix() is { } localMode ? Text(localMode.pathMode()) + " " : "";
        string? groupPath = repeatedGroup.subpathVariableDeclaration()?.subpathVariable().GetText();
        foreach (string name in groupNames)
        {
            bool node = elements[0].First(element => Binding(element) == name).nodePattern() is not null;
            if (elements.SelectMany(items => items).Where(element => Binding(element) == name)
                .Any(element => (element.nodePattern() is not null) != node))
            {
                throw new ArgumentException("Repeated group bindings must use a consistent element kind in every branch.");
            }
        }
        var nestedPaths = bodies.Select(body => body.Groups.Where(item => !ReferenceEquals(item.Group, repeatedGroup))
            .Select(item => item.Group.subpathVariableDeclaration()?.subpathVariable().GetText())
            .OfType<string>().ToArray()).ToArray();
        if (nestedPaths.Any(names => names.Length != names.Distinct(StringComparer.Ordinal).Count() ||
            names.Any(name => groupNames.Contains(name))) ||
            nestedPaths.Skip(1).Any(names => !names.ToHashSet(StringComparer.Ordinal).SetEquals(nestedPaths[0])))
        {
            throw new ArgumentException("Repeated branch subpaths require distinct names and matching path bindings in every branch.");
        }
        groupNames = [.. groupNames, .. nestedPaths[0]];
        if (groupPath is not null)
        {
            if (groupNames.Contains(groupPath))
            {
                throw new ArgumentException("A repeated subpath requires a name distinct from its element variables.");
            }
            groupNames = [.. groupNames, groupPath];
        }
        foreach (string name in groupNames)
        {
            if (declared.Contains(name) || scope.Contains(name) || exported.Contains(name) ||
                declaration.pathVariable().GetText() == name)
            {
                throw new ArgumentException("Different-length repeated group bindings require names distinct from outer scalar bindings and the path.");
            }
        }
        exported = exported.Concat(groupNames).ToArray();
        int minimumEdges = elements.Min(items => items.Length / 2);
        int maximumEdges = elements.Max(items => items.Length / 2);
        if (minimum > 10_000 / minimumEdges)
        {
            throw new ArgumentException("Repeated-group minimum exceeds 10,000 relationships.");
        }
        int? maximum = null;
        if (upper is not null)
        {
            if (!int.TryParse(NormalizeUnsignedInteger(upper), out int bound) || bound < minimum || bound > 10_000 / maximumEdges)
            {
                throw new ArgumentException("Repeated-group upper bound must cover the minimum and at most 10,000 relationships.");
            }
            maximum = bound;
        }
        string prefix = "__differentBag";
        while (source.Contains(prefix, StringComparison.Ordinal))
        {
            prefix += "_";
        }
        string pathName = declaration.pathVariable().GetText();
        string start = prefix + "Start", end = prefix + "End", position = prefix + "Position";
        string transition = prefix + "Transition", transitions = prefix + "Transitions";
        var quantifiedBindings = exterior.Where(factor => QuantifiedEdge(factor) is not null)
            .Select((factor, index) => (Factor: factor, Name:
                Descendants<GQLParser.ElementPatternFillerContext>(factor).FirstOrDefault()?.elementVariableDeclaration()?.GetText()
                    ?? prefix + "ExteriorEdges" + index))
            .ToDictionary(item => item.Factor, item => item.Name);
        string ExteriorLength(IEnumerable<GQLParser.PathFactorContext> section) => "(" +
            string.Join('+', section.Where(factor => Boundary(factor) is null).Select(factor =>
                quantifiedBindings.TryGetValue(factor, out string? name) ? "CARDINALITY(" + name + ")" : "1").Prepend("0")) + ")";
        string prefixEdges = ExteriorLength(factors[..prefixEnd]);
        string suffixEdges = ExteriorLength(factors[suffixStart..]);
        string repeatedLength = "(PATH_LENGTH(" + pathName + ")-" + prefixEdges + "-" + suffixEdges + ")";
        string repeatedEnd = "(PATH_LENGTH(" + pathName + ")-" + suffixEdges + ")";
        string selectionStart = prefix + "WholeStart", selectionEnd = prefix + "WholeEnd";
        var exteriorPredicates = new List<string>();
        var quantifiedPredicates = new List<(string List, string Element, string Condition)>();
        string ExteriorFactor(GQLParser.PathFactorContext factor)
        {
            string text = Text(factor);
            var filler = Descendants<GQLParser.ElementPatternFillerContext>(factor).FirstOrDefault();
            if (QuantifiedEdge(factor) is { } edge)
            {
                var edgePredicate = filler?.elementPatternPredicate();
                var edgeLabels = filler?.isLabelExpression();
                bool complexLabels = edgeLabels is not null && !IsSimpleLabelExpression(edgeLabels.labelExpression(), false);
                string edgeText = edge.abbreviatedEdgePattern() is { } abbreviated
                    ? ExpandRepeatedBagEdge(abbreviated.GetText()) : Text(edge);
                bool dynamicPredicate = edgePredicate is not null && (edgePredicate.elementPropertySpecification() is null ||
                    Descendants<GQLParser.BindingVariableReferenceContext>(edgePredicate).Any() ||
                    Descendants<GQLParser.ElementVariableReferenceContext>(edgePredicate).Any() ||
                    Descendants<GQLParser.NestedQuerySpecificationContext>(edgePredicate).Any());
                if (dynamicPredicate || complexLabels)
                {
                    string list = quantifiedBindings[factor];
                    string scalar = prefix + "PredicateEdge" + quantifiedPredicates.Count;
                    string Remap(ParserRuleContext expression)
                    {
                        foreach (var nested in Descendants<GQLParser.NestedQuerySpecificationContext>(expression).Cast<ParserRuleContext>()
                            .Concat(Descendants<GQLParser.NestedProcedureSpecificationContext>(expression)))
                        {
                            ValidateCorrelatedScalarQuery(nested, new HashSet<string>(StringComparer.Ordinal) { list }, pathName);
                        }
                        string value = Text(expression);
                        var references = Descendants<GQLParser.BindingVariableReferenceContext>(expression).Cast<ParserRuleContext>()
                            .Concat(PatternElementOccurrences(expression, includeExists: true))
                            .Concat(Descendants<GQLParser.ElementVariableReferenceContext>(expression))
                            .Where(reference => !IsScalarProjectionAliasReference(reference))
                            .DistinctBy(reference => (reference.Start.StartIndex, reference.Stop.StopIndex)).ToArray();
                        if (references.Any(reference => CorrelatedReferenceName(reference) == pathName))
                        {
                            throw new ArgumentException("Enclosing path references inside quantified predicates are not yet supported.");
                        }
                        foreach (var reference in references.Where(reference => CorrelatedReferenceName(reference) == list)
                            .OrderByDescending(reference => reference.Start.StartIndex))
                        {
                            value = value.Remove(reference.Start.StartIndex - expression.Start.StartIndex,
                                reference.Stop.StopIndex - reference.Start.StartIndex + 1)
                                .Insert(reference.Start.StartIndex - expression.Start.StartIndex, scalar);
                        }
                        return value;
                    }
                    string edgeCondition;
                    if (!dynamicPredicate)
                    {
                        edgeCondition = "TRUE";
                    }
                    else if (edgePredicate!.elementPatternWhereClause() is { } edgeWhere)
                    {
                        edgeCondition = Remap(edgeWhere.searchCondition());
                    }
                    else
                    {
                        var pairs = edgePredicate.elementPropertySpecification().propertyKeyValuePairList().propertyKeyValuePair();
                        if (pairs.Select(pair => GraphGqlIdentifiers.DecodeIdentifier(pair.propertyName().GetText()))
                            .Distinct(StringComparer.Ordinal).Count() != pairs.Length)
                        {
                            throw new ArgumentException("An element property specification cannot repeat a property name.");
                        }
                        edgeCondition = String.Join(" AND ", pairs.Select(pair => scalar + "." + Text(pair.propertyName()) +
                            "=(" + Remap(pair.valueExpression()) + ")"));
                    }
                    if (complexLabels)
                    {
                        edgeCondition = "(" + edgeCondition + ") AND " + scalar + " IS LABELED (" + Text(edgeLabels!.labelExpression()) + ")";
                    }
                    quantifiedPredicates.Add((list, scalar, edgeCondition));
                    var removed = new List<ParserRuleContext>();
                    if (dynamicPredicate)
                    {
                        removed.Add(edgePredicate!);
                    }
                    if (complexLabels)
                    {
                        removed.Add(edgeLabels!);
                    }
                    foreach (var part in removed.OrderByDescending(part => part.Start.StartIndex))
                    {
                        edgeText = edgeText.Remove(part.Start.StartIndex - edge.Start.StartIndex,
                            part.Stop.StopIndex - part.Start.StartIndex + 1);
                    }
                }
                if (filler?.elementVariableDeclaration() is null)
                {
                    edgeText = edgeText.Insert(edgeText.IndexOf('[') + 1, quantifiedBindings[factor] + " ");
                }
                return edgeText + Text(((GQLParser.PfQuantifiedPathPrimaryContext)factor).graphPatternQuantifier());
            }
            var predicate = filler?.elementPatternPredicate();
            if (predicate is null || predicate.elementPropertySpecification() is not null &&
                !Descendants<GQLParser.BindingVariableReferenceContext>(predicate).Any() &&
                !Descendants<GQLParser.ElementVariableReferenceContext>(predicate).Any() &&
                !Descendants<GQLParser.NestedQuerySpecificationContext>(predicate).Any())
            {
                return text;
            }
            // Boundary aliases do not exist during candidate acquisition.
            // Keep labels/static properties there, and evaluate other exterior
            // predicates after every scalar binding is available. Filtering
            // avoids re-matching an undirected edge and duplicating its rows.
            text = text.Remove(predicate.Start.StartIndex - factor.Start.StartIndex,
                predicate.Stop.StopIndex - predicate.Start.StartIndex + 1);
            string? binding = filler!.elementVariableDeclaration()?.GetText();
            if (binding is null)
            {
                binding = prefix + "Exterior" + exteriorPredicates.Count;
                text = text.Insert(Boundary(factor) is not null ? 1 : text.IndexOf('[') + 1, binding + " ");
            }
            string condition;
            if (predicate.elementPatternWhereClause() is { } where)
            {
                condition = Text(where.searchCondition());
            }
            else
            {
                var pairs = predicate.elementPropertySpecification().propertyKeyValuePairList().propertyKeyValuePair();
                if (pairs.Select(pair => GraphGqlIdentifiers.DecodeIdentifier(pair.propertyName().GetText()))
                    .Distinct(StringComparer.Ordinal).Count() != pairs.Length)
                {
                    throw new ArgumentException("An element property specification cannot repeat a property name.");
                }
                condition = String.Join(" AND ", pairs.Select(pair => binding + "." + Text(pair.propertyName()) +
                    "=(" + Text(pair.valueExpression()) + ")"));
            }
            exteriorPredicates.Add(condition);
            return text;
        }
        string prefixPattern = String.Concat(factors[..prefixEnd].Select(ExteriorFactor));
        string suffixPattern = String.Concat(factors[suffixStart..].Select(ExteriorFactor));
        string Endpoint(bool isStart)
        {
            string binding = isStart ? start : end;
            var boundary = boundaries.FirstOrDefault(value => value.Start == isStart);
            if (boundary.Node is null)
            {
                return "(" + binding + ")";
            }
            var filler = boundary.Node.elementPatternFiller();
            string labels = filler.isLabelExpression() is { } expression ? " " + Text(expression) : "";
            var predicate = filler.elementPatternPredicate();
            bool staticProperties = predicate?.elementPropertySpecification() is not null &&
                !Descendants<GQLParser.BindingVariableReferenceContext>(predicate).Any() &&
                !Descendants<GQLParser.ElementVariableReferenceContext>(predicate).Any() &&
                !Descendants<GQLParser.NestedQuerySpecificationContext>(predicate).Any();
            return "(" + binding + labels + (staticProperties ? " " + Text(predicate!) : "") + ")";
        }
        var replacements = new List<(string Source, string Target)>();
        string Placeholder(string target, bool list = false)
        {
            string value = JsonSerializer.Serialize(prefix + "Value" + replacements.Count);
            if (list)
            {
                value = "[" + value + "]";
            }
            const string alias = " AS generated";
            string normalized = Prepare("RETURN " + value + alias, depth + 1);
            replacements.Add((normalized["RETURN ".Length..^alias.Length], target));
            return value;
        }
        string matchMode = graph.matchMode() is { } mode ? Text(mode) + " " : "";
        string pathMode = path.pathPatternPrefix() is { } pathPrefix &&
            Descendants<GQLParser.PathModeContext>(pathPrefix).FirstOrDefault() is { } selectedMode ? Text(selectedMode) + " " : "";
        var search = path.pathPatternPrefix()?.pathSearchPrefix();
        bool selective = search is not null && search.allPathSearch() is null;
        var branchMatches = new List<string>();
        foreach (var branchBody in bodies)
        {
            var items = branchBody.Elements;
            int length = items.Length / 2;
            var branch = new StringBuilder("FILTER " + position + "+" + length + "<=" + repeatedEnd + " LET ");
            var pattern = new StringBuilder();
            var boundElements = new List<string>();
            var introduced = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < items.Length; index++)
            {
                string? original = Binding(items[index]);
                string binding = original ?? prefix + "Element" + index;
                if (introduced.Add(binding))
                {
                    if (introduced.Count != 1)
                    {
                        branch.Append(',');
                    }
                    branch.Append(binding).Append('=').Append(Placeholder("ELEMENTS(" + pathName + ")[2*" + position + "+" + index + "]"));
                }
                string text = Text(items[index]);
                if (items[index].edgePattern()?.abbreviatedEdgePattern() is { } abbreviated)
                {
                    text = ExpandRepeatedBagEdge(abbreviated.GetText());
                }
                int insertion = index % 2 == 0 ? 1 : text.IndexOf('[') + 1;
                string bound = original is null ? text.Insert(insertion, binding + " ") : text;
                pattern.Append(bound);
                if (items[index].nodePattern() is not null)
                {
                    boundElements.Add("(" + binding + ")");
                }
                else if (items[index].edgePattern()?.abbreviatedEdgePattern() is not null)
                {
                    boundElements.Add(text.Insert(insertion, binding));
                }
                else
                {
                    var filler = Descendants<GQLParser.ElementPatternFillerContext>(items[index]).First();
                    int offset = filler.Start.StartIndex - items[index].Start.StartIndex;
                    boundElements.Add(text.Remove(offset, Math.Max(0, filler.Stop.StopIndex - filler.Start.StartIndex + 1))
                        .Insert(offset, binding));
                }
            }
            // The fixed branch match enforces the iteration's mode separately;
            // candidate acquisition and the whole path retain their own mode.
            branch.Append(" MATCH ").Append(matchMode).Append(iterationMode).Append(pattern);
            foreach (var nested in branchBody.Groups.Where(item => !ReferenceEquals(item.Group, repeatedGroup)))
            {
                if (nested.Group.pathModePrefix() is { } nestedMode)
                {
                    // Every element is already bound to the candidate path.
                    // Validate this group's mode on exactly its own range;
                    // enclosing and iteration modes remain separately enforced.
                    branch.Append(" MATCH ").Append(matchMode).Append(Text(nestedMode.pathMode())).Append(' ')
                        .AppendJoin("", boundElements.Skip(2 * nested.Start).Take(2 * (nested.End - nested.Start) + 1));
                }
            }
            // Bind all subpaths before evaluating predicates that can refer
            // to another path binding in the same iteration.
            foreach (var nested in branchBody.Groups)
            {
                if (nested.Group.subpathVariableDeclaration()?.subpathVariable().GetText() is { } nestedName)
                {
                    branch.Append(" LET ").Append(nestedName).Append("=PATH[")
                        .AppendJoin(',', Enumerable.Range(2 * nested.Start, 2 * (nested.End - nested.Start) + 1)
                            .Select(index => Binding(items[index]) ?? prefix + "Element" + index)).Append(']');
                }
            }
            foreach (var nested in branchBody.Groups)
            {
                if (nested.Group.parenthesizedPathPatternWhereClause()?.searchCondition() is { } condition)
                {
                    branch.Append(" FILTER ").Append(Text(condition));
                }
            }
            branch.Append(" RETURN [").Append(position).Append('-').Append(prefixEdges).Append(',')
                .Append(position).Append('-').Append(prefixEdges).Append('+').Append(length)
                .Append(",1");
            if (groupNames.Length != 0)
            {
                branch.Append(",[").AppendJoin(',', groupNames).Append(']');
            }
            branch.Append("] AS ").Append(transition);
            branchMatches.Add(branch.ToString());
        }
        // Any edge in a valid decomposition must satisfy at least one branch
        // position's direction and label constraint. Retain full branch checks
        // below; this union only reduces unnecessary candidate traversal.
        string candidateEdge = RepeatedBagCandidateEdge(elements.SelectMany(items => items)
            .Select(item => item.edgePattern()).OfType<GQLParser.EdgePatternContext>().ToArray(), "",
            simpleLabelsOnly: quantifiedBindings.Count != 0);
        var body = new StringBuilder("MATCH " + matchMode + pathName + "=" + pathMode + prefixPattern + Endpoint(true) + candidateEdge + "{" +
            (minimum * minimumEdges) + "," + (maximum is { } max ? (max * maximumEdges).ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + "}" + Endpoint(false) + suffixPattern);
        if (boundaries.Count == 2 && boundaries[0].Name is { } firstName && firstName == boundaries[1].Name)
        {
            body.Append(" FILTER ELEMENT_ID(").Append(start).Append(")=ELEMENT_ID(").Append(end).Append(')');
        }
        foreach (var boundary in boundaries.Where(value => value.Name is not null &&
            (declared.Contains(value.Name) || exteriorNames.Contains(value.Name))))
        {
            body.Append(" FILTER ELEMENT_ID(").Append(boundary.Start ? start : end)
                .Append(")=ELEMENT_ID(").Append(boundary.Name).Append(')');
        }
        if (anchorAliases.Length > 0)
        {
            body.Append(" LET ").AppendJoin(',', anchorAliases.Select(name => name + "=" +
                (boundaries.First(value => value.Name == name).Start ? start : end)));
        }
        body.Append(" CALL { FOR ").Append(position).Append(" IN ")
            .Append(Placeholder("RANGE(" + prefixEdges + "," + repeatedEnd + "-1)", list: true));
        body.Append(" CALL { ").AppendJoin(" UNION ALL ", branchMatches).Append(" } RETURN COLLECT_LIST(")
            .Append(transition).Append(") AS ").Append(transitions).Append(" } FOR ").Append(prefix).Append("Copy IN ");
        string operands = repeatedLength + "," + minimum + "," +
            (maximum?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL") + "," + transitions;
        body.Append(Placeholder(groupNames.Length == 0 ? "RANGE(1,__GQL_PATH_MULTIPLICITY(" + operands + "))"
            : "__GQL_PATH_DECOMPOSITIONS(" + operands + ")", list: true));
        if (groupNames.Length != 0)
        {
            body.Append(" CALL { FOR ").Append(prefix).Append("Step IN ").Append(prefix).Append("Copy LET ");
            for (int index = 0; index < groupNames.Length; index++)
            {
                if (index != 0)
                {
                    body.Append(',');
                }
                body.Append(prefix).Append("Field").Append(index).Append('=')
                    .Append(Placeholder(prefix + "Step[" + index + "]"));
            }
            body.Append(" RETURN ").AppendJoin(',', groupNames.Select((name, index) =>
                "COLLECT_LIST(" + prefix + "Field" + index + ") AS " + name)).Append(" }");
        }
        foreach (var boundary in boundaries)
        {
            string constraint = Text(boundary.Node);
            if (boundary.Name is null)
            {
                constraint = constraint.Insert(1, (boundary.Start ? start : end) + " ");
            }
            body.Append(" MATCH ").Append(constraint);
        }
        foreach (string condition in exteriorPredicates)
        {
            body.Append(" FILTER (").Append(condition).Append(')');
        }
        foreach (string condition in exteriorGroupPredicates)
        {
            body.Append(" FILTER (").Append(condition).Append(')');
        }
        foreach (var predicate in quantifiedPredicates)
        {
            string valid = predicate.Element + "Valid";
            body.Append(" CALL { FOR ").Append(predicate.Element).Append(" IN ").Append(predicate.List)
                .Append(" FILTER (").Append(predicate.Condition).Append(") RETURN COUNT(*) AS ").Append(valid)
                .Append(" } FILTER ").Append(valid).Append("=CARDINALITY(").Append(predicate.List).Append(')');
        }
        if (!selective && graph.graphPatternWhereClause() is { } where)
        {
            body.Append(" FILTER ").Append(Text(where.searchCondition()));
        }
        if (selective)
        {
            body.Append(" LET ").Append(selectionStart).Append('=').Append(Placeholder("ELEMENTS(" + pathName + ")[0]"))
                .Append(',').Append(selectionEnd).Append('=').Append(Placeholder("ELEMENTS(" + pathName + ")[2*PATH_LENGTH(" + pathName + ")]"));
        }
        body.Append(" RETURN ").Append(pathName);
        foreach (string name in exported)
        {
            body.Append(',').Append(name);
        }
        if (selective)
        {
            body.Append(',').Append(selectionStart).Append(',').Append(selectionEnd);
        }
        string expanded = "CALL { " + body + " }";
        if (selective)
        {
            expanded = SelectPathRows(expanded, new[] { pathName }.Concat(exported).ToArray(), pathName, selectionStart, selectionEnd, search!,
                graph.graphPatternWhereClause() is { } postfilter ? Text(postfilter.searchCondition()) : null, prefix);
        }
        string prepared = PrepareWithElementScope(expanded, declared, depth + 1, scope);
        foreach (var replacement in replacements)
        {
            if (!prepared.Contains(replacement.Source, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Generated path decomposition operand was not preserved during lowering.");
            }
            prepared = prepared.Replace(replacement.Source, replacement.Target, StringComparison.Ordinal);
        }
        return prepared;
    }
}
