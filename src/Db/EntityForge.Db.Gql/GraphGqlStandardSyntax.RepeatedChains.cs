using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using EntityForge.Db.Query.Syntax.Gql;
using EntityForge.Query.Querying;
using System.Text;
using System.Text.RegularExpressions;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    private static string? TryExpandRepeatedChain(GQLParser.SimpleMatchStatementContext statement, string source, int depth)
    {
        string Text(ParserRuleContext context) => source[context.Start.StartIndex..(context.Stop.StopIndex + 1)];
        var table = statement.graphPatternBindingTable();
        var graph = table.graphPattern();
        var paths = graph.pathPatternList().pathPattern();
        if (table.graphPatternYieldClause() is not null || graph.keepClause() is not null || paths.Length != 1 ||
            paths[0].pathPatternExpression() is not GQLParser.PpePathTermContext pathTerm)
        {
            return null;
        }
        var outerFactors = pathTerm.pathTerm().pathFactor();
        var repeated = outerFactors.OfType<GQLParser.PfQuantifiedPathPrimaryContext>().ToArray();
        if (repeated.Length != 1 ||
            repeated[0].pathPrimary() is not GQLParser.PpParenthesizedPathPatternExpressionContext primary)
        {
            return null;
        }
        var quantified = repeated[0];
        int repeatedIndex = Array.IndexOf(outerFactors, quantified);
        GQLParser.NodePatternContext? Boundary(GQLParser.PathFactorContext factor) =>
            factor is GQLParser.PfPathPrimaryContext plain && plain.pathPrimary() is GQLParser.PpElementPatternContext element
                ? element.elementPattern().nodePattern() : null;
        var boundaries = new List<(GQLParser.NodePatternContext Node, string? Name, bool Start)>();
        int prefixEnd = repeatedIndex, suffixStart = repeatedIndex + 1;
        if (repeatedIndex > 0 && Boundary(outerFactors[repeatedIndex - 1]) is { } before)
        {
            boundaries.Add((before, before.elementPatternFiller().elementVariableDeclaration()?.GetText(), true));
            prefixEnd--;
        }
        if (suffixStart < outerFactors.Length && Boundary(outerFactors[suffixStart]) is { } after)
        {
            boundaries.Add((after, after.elementPatternFiller().elementVariableDeclaration()?.GetText(), false));
            suffixStart++;
        }
        var exteriorFactors = outerFactors[..prefixEnd].Concat(outerFactors[suffixStart..]).ToArray();
        int exteriorEdges = 0;
        foreach (var factor in exteriorFactors)
        {
            if (factor is not GQLParser.PfPathPrimaryContext plain ||
                plain.pathPrimary() is not GQLParser.PpElementPatternContext element)
            {
                return null;
            }
            exteriorEdges += element.elementPattern().edgePattern() is null ? 0 : 1;
        }
        string prefixPattern = String.Concat(outerFactors[..prefixEnd].Select(Text));
        string suffixPattern = String.Concat(outerFactors[suffixStart..].Select(Text));
        var group = primary.parenthesizedPathPatternExpression();
        var bagTerms = (group.pathPatternExpression() as GQLParser.PpeMultisetAlternationContext)?.pathTerm();
        if (bagTerms is not null && !CompatibleRepeatedBagTerms(bagTerms))
        {
            return null;
        }
        ParserRuleContext scopeContext = statement.Parent is GQLParser.OptionalOperandContext operand &&
            operand.Parent is GQLParser.OptionalMatchStatementContext optional ? optional : statement;
        var previouslyBound = PreviouslyDeclaredElements(statement, source, depth).ToHashSet(StringComparer.Ordinal);
        previouslyBound.UnionWith(CompositeScopeBefore(scopeContext, source, depth, null));
        var boundAnchors = previouslyBound
            .Where(name => boundaries.Any(boundary => boundary.Name == name)).ToHashSet(StringComparer.Ordinal);
        if (!TryCollectRepeatedBody(group, out var factors, out var bodyGroups, bagTerms?[0]))
        {
            return null;
        }
        var subpaths = bodyGroups.Where(item => item.Group.subpathVariableDeclaration() is not null)
            .Select(item => (Name: item.Group.subpathVariableDeclaration().subpathVariable().GetText(), item.Start, item.End)).ToArray();
        var groupConditions = bodyGroups.Select(item => item.Group.parenthesizedPathPatternWhereClause()?.searchCondition())
            .OfType<GQLParser.SearchConditionContext>().ToArray();
        if (factors.Length < 3 || factors.Length % 2 != 1)
        {
            return null;
        }
        var edges = new List<(GQLParser.EdgePatternContext Edge, GQLParser.ElementVariableDeclarationContext? Variable, string? Name)>();
        var nodeNames = new List<string?>();
        var nodePatterns = new List<GQLParser.NodePatternContext>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < factors.Length; index++)
        {
            if (factors[index] is not GQLParser.PfPathPrimaryContext factor ||
                factor.pathPrimary() is not GQLParser.PpElementPatternContext element)
            {
                return null;
            }
            if (index % 2 == 0)
            {
                var node = element.elementPattern().nodePattern();
                var filler = node?.elementPatternFiller();
                string? nodeName = filler?.elementVariableDeclaration()?.GetText();
                if (filler is null ||
                    nodeName is not null && (!GraphGqlIdentifiers.Whole.IsMatch(nodeName) || !names.Add(nodeName)))
                {
                    return null;
                }
                nodeNames.Add(nodeName);
                nodePatterns.Add(node!);
                continue;
            }
            if (element.elementPattern().edgePattern() is not { } edge)
            {
                return null;
            }
            var variables = PatternDeclarations(edge).ToArray();
            if (variables.Length > 1)
            {
                return null;
            }
            string? name = variables.FirstOrDefault()?.GetText();
            if (name is not null && (!GraphGqlIdentifiers.Whole.IsMatch(name) || !names.Add(name)))
            {
                return null;
            }
            edges.Add((edge, variables.FirstOrDefault(), name));
        }
        foreach (var subpath in subpaths)
        {
            if (!GraphGqlIdentifiers.Whole.IsMatch(subpath.Name) ||
                !names.Add(subpath.Name) || previouslyBound.Contains(subpath.Name))
            {
                throw new ArgumentException("A quantified subpath variable must be new and distinct from other bindings.");
            }
        }
        if (bagTerms is null && subpaths.Length == 0 && exteriorFactors.Length == 0 && boundaries.Count == 0 && edges.Count == 1 && edges[0].Name is not null && nodePatterns.All(node => node.GetText() == "()") &&
            groupConditions.Length == 0 && !Descendants<GQLParser.ElementPatternWhereClauseContext>(edges[0].Edge).Any())
        {
            return null;
        }
        var anchorNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var boundary in boundaries)
        {
            if (boundary.Name is { } name)
            {
                if (!GraphGqlIdentifiers.Whole.IsMatch(name) || names.Contains(name) && !anchorNames.Contains(name))
                {
                    throw new ArgumentException("Repeated-group endpoint anchors require regular names distinct from group variables.");
                }
                names.Add(name);
                anchorNames.Add(name);
            }
        }
        bool closedAnchor = boundaries.Count == 2 && boundaries[0].Name is { } firstAnchor && firstAnchor == boundaries[1].Name;
        var quantifier = quantified.graphPatternQuantifier();
        var exteriorNames = exteriorFactors.SelectMany(PatternDeclarations).Select(variable => variable.GetText()).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string name in exteriorNames)
        {
            if (!names.Add(name))
            {
                throw new ArgumentException("Fixed path factors and repeated-group variables require distinct names.");
            }
        }
        var exteriorExports = exteriorNames.Except(previouslyBound, StringComparer.Ordinal).ToArray();
        var fixedBound = quantifier.fixedQuantifier()?.unsignedInteger();
        var general = quantifier.generalQuantifier();
        string lowerText = fixedBound?.GetText() ?? general?.lowerBound()?.GetText() ?? (quantifier.GetText() == "+" ? "1" : "0");
        string? upperText = fixedBound?.GetText() ?? general?.upperBound()?.GetText();
        bool unbounded = upperText is null;
        int maximum = -1;
        int maximumHops = GraphPatternLimits.MaximumSpecifiedHops;
        if (!Int32.TryParse(NormalizeUnsignedInteger(lowerText), out int minimum) || minimum < 0 || minimum > maximumHops)
        {
            throw new ArgumentException("Repeated-group minimum length must be between zero and 10,000.");
        }
        if (edges.Count > GraphGqlQueryCompiler.MaximumPatternClauses || minimum > (maximumHops - exteriorEdges) / edges.Count)
        {
            throw new ArgumentException("Repeated groups require at most 16 relationships per repetition and a minimum expanded length no greater than 10,000.");
        }
        if (!unbounded && (!Int32.TryParse(NormalizeUnsignedInteger(upperText!), out maximum) ||
            maximum < minimum || maximum > (maximumHops - exteriorEdges) / edges.Count))
        {
            throw new ArgumentException("Repeated-group upper bounds must cover the minimum and at most 10,000 expanded relationships.");
        }
        bool traverse = unbounded || maximum == 0 && bagTerms is null || maximum - minimum >= 16 ||
            maximum > (GraphGqlQueryCompiler.MaximumPatternClauses - exteriorEdges) / edges.Count;
        var path = paths[0];
        var search = path.pathPatternPrefix()?.pathSearchPrefix();
        bool selective = search is not null && search.allPathSearch() is null;
        string? pathName = path.pathVariableDeclaration()?.pathVariable().GetText();
        if (pathName is not null && (!GraphGqlIdentifiers.Whole.IsMatch(pathName) || names.Contains(pathName)))
        {
            return null;
        }
        if (names.Count == 0 && pathName is null)
        {
            return null;
        }
        int prefixIndex = 0;
        string temporaryPrefix = "__repeatedGroup0_";
        while (source.Contains(temporaryPrefix, StringComparison.Ordinal))
        {
            temporaryPrefix = "__repeatedGroup" + ++prefixIndex + "_";
        }
        string? selectedPath = selective ? pathName ?? temporaryPrefix + "Path" : pathName;
        string startColumn = temporaryPrefix + "Start", endColumn = temporaryPrefix + "End";
        string NodeAt(int position) => temporaryPrefix + "Node" + position;
        string EdgeAt(int iteration, int position) => temporaryPrefix + iteration + "_" + position;
        string IterationPathBinding(int iteration, int subpath) => temporaryPrefix + "Subpath" + iteration + "_" + subpath;
        string IterationPath(int iteration, int subpath) => "PATH[" + String.Join(',',
            Enumerable.Range(subpaths[subpath].Start, subpaths[subpath].End - subpaths[subpath].Start)
            .SelectMany(position => new[] { NodeAt(iteration * edges.Count + position), EdgeAt(iteration, position) })
            .Append(NodeAt(iteration * edges.Count + subpaths[subpath].End))) + "]";
        string? exteriorStart = null, exteriorEnd = null;
        if (selective && prefixEnd > 0)
        {
            var endpoint = Boundary(outerFactors[0]) ?? throw new ArgumentException("A fixed path prefix must begin with a node.");
            exteriorStart = endpoint.elementPatternFiller().elementVariableDeclaration()?.GetText();
            if (exteriorStart is null)
            {
                exteriorStart = temporaryPrefix + "ExteriorStart";
                prefixPattern = prefixPattern.Insert(1, exteriorStart + " ");
            }
        }
        if (selective && suffixStart < outerFactors.Length)
        {
            var endpoint = Boundary(outerFactors[^1]) ?? throw new ArgumentException("A fixed path suffix must end with a node.");
            exteriorEnd = endpoint.elementPatternFiller().elementVariableDeclaration()?.GetText();
            if (exteriorEnd is null)
            {
                exteriorEnd = temporaryPrefix + "ExteriorEnd";
                int offset = outerFactors[suffixStart..^1].Sum(factor => Text(factor).Length);
                suffixPattern = suffixPattern.Insert(offset + 1, exteriorEnd + " ");
            }
        }
        string Remap(ParserRuleContext expression, int iteration)
        {
            var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int subpath = 0; subpath < subpaths.Length; subpath++)
            {
                replacements.Add(subpaths[subpath].Name, IterationPathBinding(iteration, subpath));
            }
            for (int position = 0; position < nodeNames.Count; position++)
            {
                if (nodeNames[position] is { } name)
                {
                    replacements.Add(name, NodeAt(iteration * edges.Count + position));
                }
            }
            for (int position = 0; position < edges.Count; position++)
            {
                if (edges[position].Name is { } name)
                {
                    replacements.Add(name, EdgeAt(iteration, position));
                }
            }
            var edits = new List<(int Start, int Length, string Value)>();
            var pending = new Stack<IParseTree>();
            pending.Push(expression);
            while (pending.TryPop(out var node))
            {
                if (node is GQLParser.NestedQuerySpecificationContext nested)
                {
                    ValidateCorrelatedScalarQuery(nested, replacements.Keys.ToHashSet(StringComparer.Ordinal), pathName);
                }
                if (node is GQLParser.NestedProcedureSpecificationContext procedure)
                {
                    ValidateCorrelatedScalarQuery(procedure, replacements.Keys.ToHashSet(StringComparer.Ordinal), pathName);
                }
                if (node is GQLParser.LetValueExpressionContext or GQLParser.GraphPatternYieldClauseContext)
                {
                    throw new ArgumentException("Nested queries and local expression scopes inside repeated-group predicates are not yet supported.");
                }
                if (node is GQLParser.BindingVariableReferenceContext or GQLParser.ElementVariableReferenceContext or GQLParser.ElementVariableDeclarationContext ||
                    node is ParserRuleContext yieldReference && IsNextYieldReference(yieldReference))
                {
                    var reference = (ParserRuleContext)node;
                    if (IsScalarProjectionAliasReference(reference))
                    {
                        continue;
                    }
                    string name = CorrelatedReferenceName(reference);
                    if (name == pathName)
                    {
                        throw new ArgumentException("Enclosing path references inside repeated-group predicates are not yet supported.");
                    }
                    if (replacements.TryGetValue(name, out string? replacement))
                    {
                        edits.Add((reference.Start.StartIndex - expression.Start.StartIndex,
                            reference.Stop.StopIndex - reference.Start.StartIndex + 1, replacement));
                        continue;
                    }
                }
                for (int index = node.ChildCount - 1; index >= 0; index--)
                {
                    pending.Push(node.GetChild(index));
                }
            }
            string result = Text(expression);
            foreach (var edit in edits.OrderByDescending(edit => edit.Start))
            {
                result = result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
            }
            return result;
        }
        string Predicate(GQLParser.ElementPatternPredicateContext predicate, string binding, int iteration)
        {
            if (predicate.elementPatternWhereClause() is { } where)
            {
                return Remap(where.searchCondition(), iteration);
            }
            var pairs = predicate.elementPropertySpecification().propertyKeyValuePairList().propertyKeyValuePair();
            if (pairs.Select(pair => GraphGqlIdentifiers.DecodeIdentifier(pair.propertyName().GetText())).Distinct(StringComparer.Ordinal).Count() != pairs.Length)
            {
                throw new ArgumentException("A repeated element property specification cannot repeat a property name.");
            }
            return String.Join(" AND ", pairs.Select(pair => binding + "." + Text(pair.propertyName()) + "=(" + Remap(pair.valueExpression(), iteration) + ")"));
        }
        var branches = new List<string>();
        var generatedIndexReads = new List<(string Source, string Target)>();
        string BagChoices(int iteration, string choice) => string.Join(" UNION ALL ", bagTerms!.Select(term =>
        {
            var pattern = new StringBuilder();
            var elements = RepeatedAlternativeElements(term).ToArray();
            for (int position = 0; position < elements.Length; position++)
            {
                var element = elements[position];
                string text = Remap(element, iteration);
                if (element.edgePattern()?.abbreviatedEdgePattern() is { } abbreviated)
                {
                    text = ExpandRepeatedBagEdge(abbreviated.GetText());
                }
                var filler = Descendants<GQLParser.ElementPatternFillerContext>(element).FirstOrDefault();
                if (filler?.elementVariableDeclaration() is null)
                {
                    int insertion = position % 2 == 0 ? 1 : text.IndexOf('[') + 1;
                    text = text.Insert(insertion, (position % 2 == 0 ? NodeAt(iteration * edges.Count + position / 2) :
                        EdgeAt(iteration, position / 2)) + " ");
                }
                pattern.Append(text);
            }
            return "MATCH " + (graph.matchMode() is { } mode ? Text(mode) + " " : "") + pattern + " RETURN 1 AS " + choice;
        }));
        if (traverse)
        {
            string candidatePath = selectedPath ?? temporaryPrefix + "Path";
            int prefixEdges = outerFactors[..prefixEnd].Count(factor => Boundary(factor) is null);
            int suffixEdges = exteriorEdges - prefixEdges;
            string repeatedLength = "(PATH_LENGTH(" + candidatePath + ")-" + exteriorEdges + ")";
            string baseStart = temporaryPrefix + "BaseStart", baseEnd = temporaryPrefix + "BaseEnd";
            string item = temporaryPrefix + "Element", position = temporaryPrefix + "Position";
            string vertex = temporaryPrefix + "Vertex", vertexPosition = temporaryPrefix + "VertexPosition";
            string valid = temporaryPrefix + "Valid";
            string EdgeText(GQLParser.EdgePatternContext edge)
            {
                string edgeText = Text(edge);
                if (edge.abbreviatedEdgePattern() is not null)
                {
                    edgeText = edgeText.Trim() switch
                    {
                        "<-" => "<-[]-",
                        "->" => "-[]->",
                        "-" => "-[]-",
                        "<->" => "<-[]->",
                        "~" => "~[]~",
                        "<~" => "<~[]~",
                        "~>" => "~[]~>",
                        _ => throw new ArgumentException("Unsupported abbreviated edge pattern.")
                    };
                }
                return edgeText;
            }
            // Candidate acquisition may be broader than one iteration; the
            // bound per-iteration pattern below checks every edge's identity,
            // direction, label and predicates before a path can be selected.
            var shapes = edges.Select(value => EdgeText(value.Edge)).Select(text => text[..(text.IndexOf('[') + 1)] + text[text.LastIndexOf(']')..]).Distinct().ToArray();
            string shape = shapes.Length == 1 ? shapes[0] : "-[]-";
            var labels = edges.Select(value => Descendants<GQLParser.ElementPatternFillerContext>(value.Edge).FirstOrDefault()?.isLabelExpression()?.labelExpression().GetText()).Distinct().ToArray();
            string? label = labels.Length == 1 ? labels[0] : null;
            string labelFilter = label is not null && GraphGqlIdentifiers.Whole.IsMatch(label) ? ":" + label : "";
            string unconstrainedEdge = shape.Insert(shape.IndexOf('[') + 1, labelFilter);
            if (bagTerms is not null)
            {
                unconstrainedEdge = edges.Count == 1 ? RepeatedBagCandidateEdge(bagTerms, 0, "") : "-[]-";
            }
            string ConstrainedEdge(int index)
            {
                var edge = edges[index].Edge;
                string text = edge.abbreviatedEdgePattern() is not null ? EdgeText(edge) : Remap(edge, 0);
                return edges[index].Name is null ? text.Insert(text.IndexOf('[') + 1, EdgeAt(0, index) + " ") : text;
            }
            string ConstrainedNode(int index)
            {
                string text = Remap(nodePatterns[index], 0);
                return nodeNames[index] is null ? text.Insert(1, NodeAt(index) + " ") : text;
            }
            string ElementAt(int offset)
            {
                string index = position + (offset < 0 ? "" : "+") + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                string expression = "VALUE { FOR " + vertex + " IN ELEMENTS(" + candidatePath + ") WITH OFFSET " +
                    vertexPosition + " FILTER " + vertexPosition + "=" + index + " RETURN " + vertex + " }";
                string alias = " AS " + temporaryPrefix + "Lookup";
                string normalized = Prepare("RETURN " + expression + alias, depth + 1);
                generatedIndexReads.Add((normalized["RETURN ".Length..^alias.Length], "ELEMENTS(" + candidatePath + ")[" + index + "]"));
                return expression;
            }
            string BaseEndpoint(bool start)
            {
                string binding = start ? baseStart : baseEnd;
                var boundary = boundaries.FirstOrDefault(value => value.Start == start);
                if (boundary.Node is null)
                {
                    return "(" + binding + ")";
                }
                var filler = boundary.Node.elementPatternFiller();
                string labels = filler.isLabelExpression() is { } expression ? " " + Text(expression) : "";
                var predicate = filler.elementPatternPredicate();
                // Push static anchor constraints into candidate acquisition. A
                // predicate depending on group variables must wait for the
                // per-repetition lists to exist.
                bool staticProperties = predicate?.elementPropertySpecification() is not null &&
                    !Descendants<GQLParser.BindingVariableReferenceContext>(predicate).Any() &&
                    !Descendants<GQLParser.ElementVariableReferenceContext>(predicate).Any() &&
                    !Descendants<GQLParser.NestedQuerySpecificationContext>(predicate).Any();
                return "(" + binding + labels + (staticProperties ? " " + Text(predicate!) : "") + ")";
            }
            var branch = new StringBuilder("MATCH ");
            if (graph.matchMode() is { } matchMode)
            {
                branch.Append(Text(matchMode)).Append(' ');
            }
            branch.Append(candidatePath).Append('=');
            if (path.pathPatternPrefix() is { } prefix && Descendants<GQLParser.PathModeContext>(prefix).FirstOrDefault() is { } mode)
            {
                branch.Append(Text(mode)).Append(' ');
            }
            branch.Append(prefixPattern).Append(BaseEndpoint(true)).Append(unconstrainedEdge).Append('{')
                .Append((minimum * edges.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(unbounded ? "" : (maximum * edges.Count).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('}')
                .Append(BaseEndpoint(false)).Append(suffixPattern);
            branch.Append(" FILTER MOD(").Append(repeatedLength).Append(',').Append(edges.Count).Append(")=0");
            if (closedAnchor)
            {
                branch.Append(" FILTER ELEMENT_ID(").Append(baseStart).Append(")=ELEMENT_ID(").Append(baseEnd).Append(')');
            }
            foreach (var boundary in boundaries.Where(boundary => boundary.Name is not null && boundAnchors.Contains(boundary.Name)))
            {
                branch.Append(" FILTER ELEMENT_ID(").Append(boundary.Start ? baseStart : baseEnd)
                    .Append(")=ELEMENT_ID(").Append(boundary.Name).Append(')');
            }
            var anchorAliases = boundaries.Where(boundary => boundary.Name is not null && !boundAnchors.Contains(boundary.Name))
                .DistinctBy(boundary => boundary.Name)
                .Select(boundary => boundary.Name + "=" + (boundary.Start ? baseStart : baseEnd)).ToArray();
            if (anchorAliases.Length > 0)
            {
                branch.Append(" LET ").AppendJoin(',', anchorAliases);
            }
            branch.Append(" CALL { FOR ").Append(item).Append(" IN ELEMENTS(").Append(candidatePath).Append(") WITH OFFSET ")
                .Append(position).Append(" FILTER ").Append(item).Append(" IS TYPED EDGE AND ").Append(position)
                .Append('>').Append(2 * prefixEdges).Append(" AND ").Append(position).Append("<(PATH_LENGTH(")
                .Append(candidatePath).Append(")-").Append(suffixEdges).Append(")*2 AND MOD(").Append(position)
                .Append('-').Append(2 * prefixEdges + 1).Append(',').Append(2 * edges.Count)
                .Append(")=0 LET ").Append(EdgeAt(0, 0)).Append('=').Append(item);
            for (int index = 1; index < edges.Count; index++)
            {
                branch.Append(',').Append(EdgeAt(0, index)).Append('=').Append(ElementAt(2 * index));
            }
            for (int index = 0; index < nodePatterns.Count; index++)
            {
                branch.Append(',').Append(NodeAt(index)).Append('=').Append(ElementAt(2 * index - 1));
            }
            for (int subpath = 0; subpath < subpaths.Length; subpath++)
            {
                branch.Append(" LET ").Append(IterationPathBinding(0, subpath)).Append('=').Append(IterationPath(0, subpath));
            }
            string choiceCount = temporaryPrefix + "ChoiceCount", choiceCounts = temporaryPrefix + "ChoiceCounts";
            if (bagTerms is not null)
            {
                branch.Append(" CALL { CALL { ").Append(BagChoices(0, temporaryPrefix + "Choice"))
                    .Append(" } RETURN COUNT(*) AS ").Append(choiceCount).Append(" } FILTER ").Append(choiceCount).Append(">0");
            }
            else
            {
                branch.Append(" MATCH ");
                if (graph.matchMode() is { } iterationMode)
                {
                    branch.Append(Text(iterationMode)).Append(' ');
                }
                branch.Append(ConstrainedNode(0));
                for (int index = 0; index < edges.Count; index++)
                {
                    branch.Append(ConstrainedEdge(index)).Append(ConstrainedNode(index + 1));
                }
            }
            foreach (var condition in groupConditions)
            {
                branch.Append(" FILTER ").Append(Remap(condition, 0));
            }
            branch.Append(" RETURN COUNT(*) AS ").Append(valid);
            if (bagTerms is not null)
            {
                branch.Append(",COLLECT_LIST(").Append(choiceCount).Append(") AS ").Append(choiceCounts);
            }
            for (int subpath = 0; subpath < subpaths.Length; subpath++)
            {
                branch.Append(",COLLECT_LIST(").Append(IterationPath(0, subpath)).Append(") AS ").Append(subpaths[subpath].Name);
            }
            for (int index = 0; index < nodeNames.Count; index++)
            {
                if (nodeNames[index] is { } name)
                {
                    branch.Append(",COLLECT_LIST(").Append(NodeAt(index)).Append(") AS ").Append(name);
                }
            }
            for (int index = 0; index < edges.Count; index++)
            {
                if (edges[index].Name is { } edgeName)
                {
                    branch.Append(",COLLECT_LIST(").Append(EdgeAt(0, index)).Append(") AS ").Append(edgeName);
                }
            }
            branch.Append(" } FILTER ").Append(valid).Append('*').Append(edges.Count).Append('=').Append(repeatedLength);
            foreach (var boundary in boundaries)
            {
                string constraint = Text(boundary.Node);
                if (boundary.Name is null)
                {
                    constraint = constraint.Insert(1, (boundary.Start ? baseStart : baseEnd) + " ");
                }
                branch.Append(" MATCH ").Append(constraint);
            }
            if (!selective && graph.graphPatternWhereClause() is { } where)
            {
                branch.Append(" FILTER ").Append(Text(where.searchCondition()));
            }
            if (bagTerms is not null)
            {
                string placeholder = "[\"" + temporaryPrefix + "BagCopies\"]";
                string alias = " AS " + temporaryPrefix + "CopiesAlias";
                string normalized = Prepare("RETURN " + placeholder + alias, depth + 1);
                // REDUCE and RANGE here are internal operators after the
                // standard grammar gate. RANGE enforces its existing bounded
                // list size; numeric overflow also fails before allocation.
                generatedIndexReads.Add((normalized["RETURN ".Length..^alias.Length],
                    "RANGE(1,REDUCE(" + temporaryPrefix + "Product=1," + temporaryPrefix + "Count IN " +
                    choiceCounts + "|" + temporaryPrefix + "Product*" + temporaryPrefix + "Count))"));
                branch.Append(" FOR ").Append(temporaryPrefix).Append("Copy IN ").Append(placeholder);
            }
            var returned = edges.Select(value => value.Name).OfType<string>().Concat(nodeNames.OfType<string>())
                .Concat(subpaths.Select(subpath => subpath.Name))
                .Concat(boundaries.Select(boundary => boundary.Name).OfType<string>().Where(name => !boundAnchors.Contains(name)))
                .Concat(exteriorExports).Distinct(StringComparer.Ordinal).ToList();
            if (selectedPath is not null)
            {
                returned.Add(selectedPath);
            }
            branch.Append(" RETURN ").AppendJoin(',', returned);
            if (selective)
            {
                branch.Append(',').Append(exteriorStart ?? baseStart).Append(" AS ").Append(startColumn)
                    .Append(',').Append(exteriorEnd ?? baseEnd).Append(" AS ").Append(endColumn);
            }
            branches.Add(branch.ToString());
        }
        for (int count = minimum; !traverse && count <= maximum; count++)
        {
            var branch = new StringBuilder("MATCH ");
            if (graph.matchMode() is { } matchMode)
            {
                branch.Append(Text(matchMode)).Append(' ');
            }
            if (selectedPath is not null)
            {
                branch.Append(selectedPath).Append('=');
            }
            if (path.pathPatternPrefix() is { } prefix)
            {
                if (!selective)
                {
                    branch.Append(Text(prefix)).Append(' ');
                }
                else if (Descendants<GQLParser.PathModeContext>(prefix).FirstOrDefault() is { } mode)
                {
                    branch.Append(Text(mode)).Append(' ');
                }
            }
            branch.Append(prefixPattern).Append('(').Append(NodeAt(0)).Append(')');
            var bindings = edges.Select(_ => new List<string>()).ToArray();
            var filters = new List<string>();
            for (int iteration = 0; iteration < count; iteration++)
            {
                for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
                {
                    var (edge, variable, _) = edges[edgeIndex];
                    string temporary = EdgeAt(iteration, edgeIndex);
                    bindings[edgeIndex].Add(temporary);
                    string edgeText = Text(edge);
                    var edgePredicate = Descendants<GQLParser.ElementPatternFillerContext>(edge).FirstOrDefault()?.elementPatternPredicate();
                    if (edgePredicate is not null && bagTerms is null)
                    {
                        filters.Add(Predicate(edgePredicate, temporary, iteration));
                        edgeText = edgeText.Remove(edgePredicate.Start.StartIndex - edge.Start.StartIndex,
                            edgePredicate.Stop.StopIndex - edgePredicate.Start.StartIndex + 1);
                    }
                    if (edge.abbreviatedEdgePattern() is not null)
                    {
                        edgeText = edgeText.Trim() switch
                        {
                            "<-" => "<-[]-",
                            "->" => "-[]->",
                            "-" => "-[]-",
                            "<->" => "<-[]->",
                            "~" => "~[]~",
                            "<~" => "<~[]~",
                            "~>" => "~[]~>",
                            _ => throw new ArgumentException("Unsupported abbreviated edge pattern.")
                        };
                    }
                    if (bagTerms is not null)
                    {
                        // Acquire an identity path once; each iteration's bag
                        // branches below retain their independent matches.
                        branch.Append(RepeatedBagCandidateEdge(bagTerms, edgeIndex, temporary)).Append('(')
                            .Append(NodeAt(iteration * edges.Count + edgeIndex + 1)).Append(')');
                        continue;
                    }
                    int offset = variable is null ? edgeText.IndexOf('[') + 1 : variable.Start.StartIndex - edge.Start.StartIndex;
                    branch.Append(edgeText.Remove(offset, variable is null ? 0 : variable.Stop.StopIndex - variable.Start.StartIndex + 1).Insert(offset, temporary + " "))
                        .Append('(').Append(NodeAt(iteration * edges.Count + edgeIndex + 1)).Append(')');
                }
            }
            branch.Append(suffixPattern);
            if (subpaths.Length > 0 && count > 0)
            {
                branch.Append(" LET ").AppendJoin(',', Enumerable.Range(0, count)
                    .SelectMany(iteration => Enumerable.Range(0, subpaths.Length)
                        .Select(subpath => IterationPathBinding(iteration, subpath) + "=" + IterationPath(iteration, subpath))));
            }
            if (closedAnchor)
            {
                branch.Append(" FILTER ELEMENT_ID(").Append(NodeAt(0)).Append(")=ELEMENT_ID(").Append(NodeAt(count * edges.Count)).Append(')');
            }
            foreach (var boundary in boundaries.Where(boundary => boundary.Name is not null && boundAnchors.Contains(boundary.Name)))
            {
                branch.Append(" FILTER ELEMENT_ID(").Append(NodeAt(boundary.Start ? 0 : count * edges.Count))
                    .Append(")=ELEMENT_ID(").Append(boundary.Name).Append(')');
            }
            var anchorAliases = boundaries.Where(boundary => boundary.Name is not null && !boundAnchors.Contains(boundary.Name))
                .DistinctBy(boundary => boundary.Name)
                .Select(boundary => boundary.Name + "=" + NodeAt(boundary.Start ? 0 : count * edges.Count)).ToArray();
            if (anchorAliases.Length > 0)
            {
                branch.Append(" LET ").AppendJoin(',', anchorAliases);
            }
            for (int iteration = 0; bagTerms is null && iteration < count; iteration++)
            {
                for (int position = 0; position < nodePatterns.Count; position++)
                {
                    var node = nodePatterns[position];
                    var filler = node.elementPatternFiller();
                    var variable = filler.elementVariableDeclaration();
                    if (filler.GetText() == (variable?.GetText() ?? ""))
                    {
                        continue;
                    }
                    string constraint = Text(node);
                    string binding = NodeAt(iteration * edges.Count + position);
                    var elementPredicate = filler.elementPatternPredicate();
                    string? predicate = null;
                    if (elementPredicate is not null)
                    {
                        predicate = Predicate(elementPredicate, binding, iteration);
                        constraint = constraint.Remove(elementPredicate.Start.StartIndex - node.Start.StartIndex,
                            elementPredicate.Stop.StopIndex - elementPredicate.Start.StartIndex + 1);
                    }
                    constraint = variable is null ? constraint.Insert(1, binding + " ") :
                        constraint.Remove(variable.Start.StartIndex - node.Start.StartIndex,
                            variable.Stop.StopIndex - variable.Start.StartIndex + 1)
                            .Insert(variable.Start.StartIndex - node.Start.StartIndex, binding);
                    branch.Append(" MATCH ").Append(constraint);
                    if (predicate is not null)
                    {
                        branch.Append(" WHERE ").Append(predicate);
                    }
                }
            }
            if (bagTerms is not null)
            {
                for (int iteration = 0; iteration < count; iteration++)
                {
                    string choice = temporaryPrefix + "Choice" + iteration;
                    branch.Append(" CALL { ").Append(BagChoices(iteration, choice)).Append(" }");
                }
            }
            foreach (var condition in groupConditions)
            {
                for (int iteration = 0; iteration < count; iteration++)
                {
                    filters.Add(Remap(condition, iteration));
                }
            }
            foreach (string filter in filters)
            {
                branch.Append(" FILTER (").Append(filter).Append(')');
            }
            var declarations = edges.Select((edge, index) => (edge, index)).Where(item => item.edge.Name is not null)
                .Select(item => item.edge.Name + "=[" + String.Join(',', bindings[item.index]) + "]").ToList();
            declarations.AddRange(nodeNames.Select((name, position) => (name, position)).Where(item => item.name is not null)
                .Select(item => item.name + "=[" + String.Join(',', Enumerable.Range(0, count).Select(iteration => NodeAt(iteration * edges.Count + item.position))) + "]"));
            for (int subpath = 0; subpath < subpaths.Length; subpath++)
            {
                int selected = subpath;
                declarations.Add(subpaths[subpath].Name + "=[" + String.Join(',', Enumerable.Range(0, count)
                    .Select(iteration => IterationPath(iteration, selected))) + "]");
            }
            if (declarations.Count > 0)
            {
                branch.Append(" LET ").AppendJoin(',', declarations);
            }
            foreach (var boundary in boundaries)
            {
                string constraint = Text(boundary.Node);
                if (boundary.Name is null)
                {
                    constraint = constraint.Insert(1, NodeAt(boundary.Start ? 0 : count * edges.Count) + " ");
                }
                branch.Append(" MATCH ").Append(constraint);
            }
            if (!selective && graph.graphPatternWhereClause() is { } where)
            {
                branch.Append(" FILTER ").Append(Text(where.searchCondition()));
            }
            var returned = edges.Select(edge => edge.Name).OfType<string>().Concat(nodeNames.OfType<string>())
                .Concat(subpaths.Select(subpath => subpath.Name))
                .Concat(boundaries.Select(boundary => boundary.Name).OfType<string>().Where(name => !boundAnchors.Contains(name)))
                .Concat(exteriorExports).Distinct(StringComparer.Ordinal).ToList();
            if (selectedPath is not null)
            {
                returned.Add(selectedPath);
            }
            branch.Append(" RETURN ").AppendJoin(',', returned);
            if (selective)
            {
                branch.Append(',').Append(exteriorStart ?? NodeAt(0)).Append(" AS ").Append(startColumn)
                    .Append(',').Append(exteriorEnd ?? NodeAt(count * edges.Count)).Append(" AS ").Append(endColumn);
            }
            branches.Add(branch.ToString());
        }
        string expanded = "CALL { " + String.Join(" UNION ALL ", branches) + " }";
        if (selective)
        {
            var columns = edges.Select(edge => edge.Name).OfType<string>().Concat(nodeNames.OfType<string>())
                .Concat(subpaths.Select(subpath => subpath.Name))
                .Concat(boundaries.Select(boundary => boundary.Name).OfType<string>().Where(name => !boundAnchors.Contains(name)))
                .Concat(exteriorExports).Distinct(StringComparer.Ordinal).ToList();
            if (pathName is not null)
            {
                columns.Add(pathName);
            }
            expanded = SelectPathRows(expanded, columns, selectedPath!, startColumn, endColumn, search!,
                graph.graphPatternWhereClause() is { } postfilter ? Text(postfilter.searchCondition()) : null, temporaryPrefix);
        }
        if (expanded.Length > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("Expanded repeated group exceeds the query character limit.");
        }
        string prepared = Prepare(expanded, depth + 1);
        foreach (var lookup in generatedIndexReads)
        {
            // Lower only these compiler-generated, single-element lookups to
            // the native scalar index operator. Public GQL still passes the
            // standard grammar gate, which rejects list-index syntax. Avoid
            // charging two nested query invocations for every traversed edge.
            // The generated prefix is absent from the caller's source.
            if (!prepared.Contains(lookup.Source, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Generated path-element lookup was not preserved during lowering.");
            }
            prepared = prepared.Replace(lookup.Source, lookup.Target, StringComparison.Ordinal);
        }
        return prepared;
    }

    private static IEnumerable<string> PreviouslyDeclaredElements(ParserRuleContext statement, string source, int depth)
    {
        static IParseTree Scope(IParseTree node)
        {
            while (node.Parent is { } parent)
            {
                node = parent;
                if (node is GQLParser.LinearQueryStatementContext or GQLParser.LinearDataModifyingStatementContext or
                    GQLParser.StatementBlockContext or GQLParser.ExistsPredicateContext or
                    GQLParser.NestedQuerySpecificationContext or GQLParser.NestedProcedureSpecificationContext)
                {
                    break;
                }
            }
            return node;
        }
        var scope = Scope(statement);
        IEnumerable<string> projected = [];
        for (var parent = scope.Parent; parent is not null && parent is not GQLParser.StatementBlockContext; parent = parent.Parent)
        {
            if (parent is GQLParser.NextStatementContext)
            {
                // NEXT exposes the preceding projection, including aliases and
                // YIELD, rather than every binding from the earlier statement.
                // Inspect the incoming table at the start of this linear
                // stage. The caller can be inside an element predicate, whose
                // preceding text is not a complete MATCH statement.
                projected = CompositeScopeBefore((ParserRuleContext)scope, source, depth, null);
                break;
            }
        }
        IEnumerable<string> imports = [];
        for (var parent = scope; parent is not null; parent = parent.Parent)
        {
            if (parent is GQLParser.InlineProcedureCallContext call)
            {
                // Imported endpoint bindings already exist in this procedure.
                // Stop at the nearest call: an empty scope must not capture
                // bindings from an enclosing procedure's imports.
                imports = call.variableScopeClause() is { } explicitScope
                    ? (explicitScope.bindingVariableReferenceList()?.bindingVariableReference() ?? []).Select(reference => reference.GetText())
                    : PreviouslyDeclaredElements(call, source, depth);
                break;
            }
            if (parent is GQLParser.ValueQueryExpressionContext or GQLParser.ExistsPredicateContext)
            {
                imports = PreviouslyDeclaredElements((ParserRuleContext)parent, source, depth);
                break;
            }
        }
        var elements = Descendants<GQLParser.ElementVariableDeclarationContext>(scope)
            .Where(declaration => declaration.Start.StartIndex < statement.Start.StartIndex && ReferenceEquals(Scope(declaration), scope))
            .Select(declaration => declaration.GetText());
        var lets = Descendants<GQLParser.LetStatementContext>(scope)
            .Where(declaration => declaration.Stop.StopIndex < statement.Start.StartIndex && ReferenceEquals(Scope(declaration), scope))
            .SelectMany(declaration => declaration.letVariableDefinitionList().letVariableDefinition())
            .Select(declaration => (declaration.bindingVariable() ?? declaration.valueVariableDefinition().bindingVariable()).GetText());
        var iterations = Descendants<GQLParser.ForStatementContext>(scope)
            .Where(declaration => declaration.Stop.StopIndex < statement.Start.StartIndex && ReferenceEquals(Scope(declaration), scope))
            .Select(declaration => declaration.forItem().forItemAlias().bindingVariable().GetText());
        return imports.Concat(projected).Concat(elements).Concat(lets).Concat(iterations);
    }

    private static string PrepareWithElementScope(string source, IEnumerable<string> elements, int depth,
        IReadOnlySet<string>? initialScope = null)
    {
        string[] names = elements.Where(name => GraphGqlIdentifiers.Whole.IsMatch(name))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length == 0)
        {
            return Prepare(source, depth, initialScope);
        }
        // Supply declaration context while reparsing a rewritten statement.
        // This prefix is removed before execution: it must never acquire nodes
        // or change a relationship binding into a node binding at runtime.
        string prefix = "MATCH (" + String.Join("),(", names) + ") ";
        string prepared = Prepare(prefix + source, depth, initialScope);
        if (!prepared.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Synthetic element scope was changed during statement lowering.");
        }
        return prepared[prefix.Length..];
    }

    private static IEnumerable<GQLParser.ElementVariableDeclarationContext> PatternDeclarations(IParseTree root)
        => PatternElementOccurrences(root, includeExists: false);

    // Inside graph-pattern EXISTS, an element declaration can refer to an
    // already bound element. Include these occurrences when discovering CALL
    // imports, while keeping them out of the enclosing pattern's exports.
    private static IEnumerable<GQLParser.ElementVariableDeclarationContext> PatternElementOccurrences(IParseTree root, bool includeExists)
    {
        var pending = new Stack<IParseTree>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if ((!includeExists && node is GQLParser.ExistsPredicateContext) ||
                node is GQLParser.NestedQuerySpecificationContext or GQLParser.NestedProcedureSpecificationContext)
            {
                continue;
            }
            if (node is GQLParser.ElementVariableDeclarationContext declaration)
            {
                yield return declaration;
            }
            for (int index = node.ChildCount - 1; index >= 0; index--)
            {
                pending.Push(node.GetChild(index));
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(IParseTree root) where T : IParseTree
    {
        var pending = new Stack<IParseTree>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is T found)
            {
                yield return found;
            }
            for (int index = node.ChildCount - 1; index >= 0; index--)
            {
                pending.Push(node.GetChild(index));
            }
        }
    }
}
