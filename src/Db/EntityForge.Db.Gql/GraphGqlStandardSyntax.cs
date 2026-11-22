using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using System.Globalization;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal static partial class GraphGqlStandardSyntax
{
    internal static void Validate(string source)
    {
        var root = GqlSyntaxParser.Parse(source, out _);
        ValidateProjectionAliases(root);
        ValidatePathQuantifiers(root);
        ValidateElementPropertyNames(root);
        ValidateGraphPatternYield(root);
    }

    internal static string Prepare(string source) => Prepare(source, out _);

    private static string Prepare(string source, out bool omitsResult)
    {
        var root = GqlSyntaxParser.Parse(source, out var tokens);
        return Prepare(source, root, tokens, out omitsResult);
    }

    private static string Prepare(string source, GQLParser.GqlProgramContext root, CommonTokenStream tokens,
        out bool omitsResult)
    {
        ValidateProjectionAliases(root);
        ValidatePathQuantifiers(root);
        ValidateElementPropertyNames(root);
        ValidateGraphPatternYield(root);
        omitsResult = OmitsResult(root);
        string bound = BindSessionUser(source, "anonymous");
        return Prepare(bound, 0, parsed: bound == source ? (root, tokens) : null);
    }

    private static void ValidateGraphPatternYield(IParseTree root)
    {
        if (Descendants<GQLParser.GraphPatternYieldClauseContext>(root).Any())
        {
            throw InvalidSyntax("Graph-pattern YIELD (GQ19) is not supported; use RETURN followed by NEXT to project working-table bindings.");
        }
    }

    private static void ValidateElementPropertyNames(IParseTree root)
    {
        foreach (var properties in Descendants<GQLParser.ElementPropertySpecificationContext>(root))
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in properties.propertyKeyValuePairList().propertyKeyValuePair())
            {
                if (!names.Add(GraphGqlIdentifiers.DecodeIdentifier(pair.propertyName().GetText())))
                {
                    throw InvalidSyntax("Element property names must be distinct.");
                }
            }
        }
    }

    private static void ValidateProjectionAliases(IParseTree root)
    {
        foreach (var item in Descendants<GQLParser.ReturnItemContext>(root))
        {
            if (item.returnItemAlias() is not null)
            {
                continue;
            }
            IParseTree expression = item.aggregatingValueExpression();
            while (expression.ChildCount == 1 && expression is not GQLParser.BindingVariableReferenceContext)
            {
                expression = expression.GetChild(0);
            }
            if (expression is not GQLParser.BindingVariableReferenceContext)
            {
                throw InvalidSyntax("A computed RETURN item requires an explicit AS alias; only a binding variable can omit its alias.");
            }
        }
    }

    private static string Prepare(string source, int depth, IReadOnlySet<string>? initialScope = null,
        (GQLParser.GqlProgramContext Root, CommonTokenStream Tokens)? parsed = null)
    {
        if (depth > 16)
        {
            throw new ArgumentException("Nested pattern predicates exceed 16 lowering levels.");
        }
        GQLParser.GqlProgramContext root;
        CommonTokenStream tokens;
        if (parsed is { } existing)
        {
            (root, tokens) = existing;
        }
        else
        {
            root = GqlSyntaxParser.Parse(source, out tokens);
        }
        var outerFocused = Descendants<GQLParser.FocusedNestedQuerySpecificationContext>(root)
            .FirstOrDefault(focused => focused.Start.StartIndex == root.Start.StartIndex &&
                focused.Stop.TokenIndex == tokens.GetTokens().Last(token => token.Type != TokenConstants.EOF &&
                    token.Channel == TokenConstants.DefaultChannel).TokenIndex);
        if (outerFocused is not null)
        {
            // An enclosing focused query has no following scope that could
            // observe its locals. Its braces can be removed without exporting
            // additional bindings or changing the selected graph.
            var nested = outerFocused.nestedQuerySpecification();
            source = source.Remove(nested.Stop.StartIndex, 1).Remove(nested.Start.StartIndex, 1);
            return Prepare(source, depth + 1, initialScope);
        }
        // Compile the same text that the standard parser sees. In particular,
        // -- introduces a comment, not a Cypher abbreviated relationship.
        // Normalize lexer-classified separators too: the execution compiler's
        // .NET whitespace classes do not cover every separator in the grammar.
        // Token spans keep literal and delimited-identifier contents intact.
        char[] characters = source.ToCharArray();
        foreach (var token in tokens.GetTokens().Where(token => token.Type is
            GQLLexer.SP or GQLLexer.WHITESPACE or GQLLexer.BRACKETED_COMMENT or
            GQLLexer.SIMPLE_COMMENT_SOLIDUS or GQLLexer.SIMPLE_COMMENT_MINUS))
        {
            for (int index = token.StartIndex; index <= token.StopIndex; index++)
            {
                if (characters[index] is not ('\r' or '\n'))
                {
                    characters[index] = ' ';
                }
            }
        }
        source = new string(characters);
        if ((NormalizeLetValueExpression(root, source) ?? NormalizeUntypedLetDeclarations(root, source) ?? NormalizeQuestionedPaths(root, source) ?? NormalizeRepeatedLabelAlternatives(root, source) ?? NormalizeFixedExteriorPathAlternatives(root, source) ?? NormalizeTransparentPathGroups(root, source) ?? NormalizeEmptyAdjacentNodes(root, source) ?? NormalizeAdjacentNodeConstraints(root, source) ??
            NormalizeWholeSubpathModes(root, source) ?? NormalizeWholeSubpathBindings(root, source) ??
            NormalizeQuantifiedSubpathModes(root, source) ?? NormalizeFixedSubpathModes(root, source) ?? NormalizeSelectedSubpathPredicates(root, source)) is { } subpathSource)
        {
            return Prepare(subpathSource, depth + 1, initialScope);
        }
        if (NormalizeRepeatedSegmentAnchors(root, source) is { } anchoredSource)
        {
            return Prepare(anchoredSource, depth + 1, initialScope);
        }
        if (NormalizeDirectedAlternatives(root, source) is { } directedSource)
        {
            return Prepare(directedSource, depth + 1, initialScope);
        }
        var encodedProperties = Descendants<GQLParser.PropertyNameContext>(root)
            .Where(property => property.GetText().StartsWith('`') ||
                property.identifier().regularIdentifier() is not null && property.GetText().Any(character => character > 127))
            .Cast<ParserRuleContext>()
            .Concat(Descendants<GQLParser.FieldNameContext>(root).Where(field => field.GetText().StartsWith('`') ||
                field.identifier().regularIdentifier() is not null && field.GetText().Any(character => character > 127)))
            .Concat(Descendants<GQLParser.LabelNameContext>(root).Where(label => label.GetText().StartsWith('`')))
            .Concat(Descendants<GQLParser.ReturnItemAliasContext>(root).Select(alias => alias.identifier()).Where(alias => alias.GetText().StartsWith('`')))
            .Concat(Descendants<GQLParser.YieldItemNameContext>(root).Where(name => name.GetText().StartsWith('`')))
            // A YIELD item contains a field name with the same source span.
            // Normalize that identifier once, before replacement changes its length.
            .DistinctBy(property => (property.Start.StartIndex, property.Stop.StopIndex))
            .OrderByDescending(property => property.Start.StartIndex).ToArray();
        if (encodedProperties.Length != 0)
        {
            foreach (var property in encodedProperties)
            {
                string spelling = property.GetText();
                string name = spelling.StartsWith('`') ? GraphGqlIdentifiers.DecodeIdentifier(spelling) : spelling;
                source = source.Remove(property.Start.StartIndex, property.Stop.StopIndex - property.Start.StartIndex + 1)
                    .Insert(property.Start.StartIndex, System.Text.Json.JsonSerializer.Serialize(name));
            }
            // Keep the replacement in standard syntax so nested pattern lowering
            // can parse it again without introducing an internal expression form.
            return Prepare(source, depth + 1, initialScope);
        }
        var transparentGroups = Descendants<GQLParser.ParenthesizedPathPatternExpressionContext>(root)
            .Where(group => group.Parent?.Parent is GQLParser.PfPathPrimaryContext &&
                group.subpathVariableDeclaration() is null && group.pathModePrefix() is null &&
                group.parenthesizedPathPatternWhereClause() is null &&
                group.pathPatternExpression() is GQLParser.PpePathTermContext && InsideRepetition(group)).ToArray();
        if (transparentGroups.Length != 0)
        {
            // Plain parentheses introduce no group bindings or path mode. Strip
            // them before inspecting a repeated chain's node/edge sequence.
            foreach (int position in transparentGroups.SelectMany(group => new[] { group.Start.StartIndex, group.Stop.StartIndex }).OrderDescending())
            {
                source = source.Remove(position, 1);
            }
            return Prepare(source, depth + 1, initialScope);
        }
        static bool InsideRepetition(IParseTree node)
        {
            for (var parent = node.Parent; parent is not null && parent is not GQLParser.GraphPatternContext; parent = parent.Parent)
            {
                if (parent is GQLParser.PfQuantifiedPathPrimaryContext)
                {
                    return true;
                }
            }
            return false;
        }
        var edits = new List<(int Start, int Length, string Text)>();
        var patternPredicates = new Dictionary<GQLParser.GraphPatternContext, List<string>>();
        void TightPunctuation(IToken first, IToken last, string replacement)
        {
            int start = first.StartIndex, end = last.StopIndex + 1;
            while (start > 0 && Char.IsWhiteSpace(source[start - 1]))
            {
                start--;
            }
            while (end < source.Length && Char.IsWhiteSpace(source[end]))
            {
                end++;
            }
            edits.Add((start, end - start, replacement));
        }
        string PreparePatternPredicate(GQLParser.SearchConditionContext condition, string prefix, string suffix)
        {
            IParseTree? ancestor = condition.Parent;
            while (ancestor is not null && ancestor is not GQLParser.GraphPatternContext)
            {
                ancestor = ancestor.Parent;
            }
            var elements = PreviouslyDeclaredElements(condition, source, depth)
                .Concat(ancestor is null ? [] : PatternDeclarations(ancestor).Select(variable => variable.GetText()));
            // Reparse the predicate with its actual endpoint bindings. Otherwise
            // a nested repeated MATCH can lower a correlated endpoint as a new
            // LET variable and collide with the runtime's imported binding.
            return PrepareWithElementScope(prefix + source[condition.Start.StartIndex..(condition.Stop.StopIndex + 1)] + suffix,
                elements, depth + 1, initialScope);
        }
        void AddPatternPredicate(ParserRuleContext context, string predicate)
        {
            IParseTree? ancestor = context.Parent;
            while (ancestor is not null && ancestor is not GQLParser.GraphPatternContext)
            {
                if (ancestor is GQLParser.PathPatternContext pathPattern &&
                    pathPattern.pathPatternPrefix()?.pathSearchPrefix() is { } search && search.allPathSearch() is null)
                {
                    // WHERE follows path selection; moving an element restriction
                    // there could discard the selected path instead of selecting
                    // an eligible alternative.
                    throw new ArgumentException("Complex pattern labels and element predicates with a selective path search are not yet supported.");
                }
                if (ancestor is GQLParser.PfQuantifiedPathPrimaryContext or GQLParser.PfQuestionedPathPrimaryContext)
                {
                    throw new ArgumentException("Complex labels and element predicates in quantified paths are not yet supported.");
                }
                ancestor = ancestor.Parent;
            }
            if (ancestor is not GQLParser.GraphPatternContext pattern)
            {
                throw new ArgumentException("An element predicate requires a graph pattern.");
            }
            if (!patternPredicates.TryGetValue(pattern, out var predicates))
            {
                patternPredicates.Add(pattern, predicates = []);
            }
            predicates.Add(predicate);
        }
        var pending = new Stack<IParseTree>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is GQLParser.UseGraphClauseContext useGraph)
            {
                if (useGraph.graphExpression().currentGraph() is null)
                {
                    throw new ArgumentException("Only USE CURRENT_GRAPH and USE CURRENT_PROPERTY_GRAPH are supported; selecting another graph is not yet supported.");
                }
                // The request supplies the current graph. Selecting it again
                // preserves that graph in this statement or nested procedure.
                edits.Add((useGraph.Start.StartIndex, useGraph.Stop.StopIndex - useGraph.Start.StartIndex + 1, " "));
                continue;
            }
            if (node is GQLParser.RepeatableElementsMatchModeContext or GQLParser.DifferentEdgesMatchModeContext)
            {
                var mode = (ParserRuleContext)node;
                bool repeatable = node is GQLParser.RepeatableElementsMatchModeContext;
                IParseTree? enclosing = node.Parent;
                while (enclosing is not null && enclosing is not GQLParser.GraphPatternContext)
                {
                    enclosing = enclosing.Parent;
                }
                if (repeatable && enclosing is GQLParser.GraphPatternContext graph &&
                    graph.pathPatternList().pathPattern() is [var onlyPath] &&
                    onlyPath.pathPatternPrefix() is { } prefix &&
                    Descendants<GQLParser.PathModeContext>(prefix).FirstOrDefault()?.GetText().ToUpperInvariant() is "TRAIL" or "ACYCLIC")
                {
                    // In a single TRAIL or ACYCLIC path, relationship uniqueness
                    // already follows from the path mode. Enforce it during
                    // acquisition so unbounded traversal remains finite. Do not
                    // apply this to multiple paths (which may share an edge),
                    // or SIMPLE (a closed two-hop path may reuse one edge).
                    repeatable = false;
                }
                edits.Add((mode.Start.StartIndex, mode.Stop.StopIndex - mode.Start.StartIndex + 1,
                    repeatable ? "REPEATABLE ELEMENTS" : "DIFFERENT RELATIONSHIPS"));
                continue;
            }
            if (node is GQLParser.StatementBlockContext compositeBlock &&
                TryExpandCompositeNext(compositeBlock, source, depth, initialScope) is { } expandedBlock)
            {
                edits.Add((compositeBlock.Start.StartIndex, compositeBlock.Stop.StopIndex - compositeBlock.Start.StartIndex + 1, expandedBlock));
                continue;
            }
            if (node is GQLParser.OptionalMatchStatementContext optionalStatement &&
                optionalStatement.optionalOperand().matchStatementBlock() is { } optionalBlock)
            {
                string expanded = ExpandOptionalBlock(optionalStatement, optionalBlock, source, depth, initialScope);
                edits.Add((optionalStatement.Start.StartIndex, optionalStatement.Stop.StopIndex - optionalStatement.Start.StartIndex + 1, expanded));
                continue;
            }
            if (node is GQLParser.SimpleMatchStatementContext matchStatement &&
                (TryExpandPathAlternatives(matchStatement, source, depth, initialScope) ??
                    TryExpandAdjacentNodeAliases(matchStatement, source, depth, initialScope) ??
                    TryExpandAnonymousWholePathModes(matchStatement, source, depth, initialScope) ??
                    TryExpandNamedFixedSubpaths(matchStatement, source, depth, initialScope) ??
                    TryExpandAnonymousSubpathModes(matchStatement, source, depth, initialScope) ??
                    TryExpandWholePathPredicates(matchStatement, source, depth, initialScope) ??
                    TryExpandSelectedPatternProduct(matchStatement, source, depth, initialScope) ??
                    TryExpandRepeatedPatternProduct(matchStatement, source, depth, initialScope) ??
                    TryExpandAnonymousRepeatedLabels(matchStatement, source, depth, initialScope) ??
                    TryExpandAnonymousPath(matchStatement, source, depth, initialScope) ??
                    TryExpandPropertyExpressions(matchStatement, source, depth) ??
                    TryExpandQuantifiedPredicates(matchStatement, source, depth) ??
                    TryExpandSelectivePredicates(matchStatement, source, depth) ??
                    TryExpandSimpleRepeatedSequence(matchStatement, source, depth) ??
                    TryExpandRepeatedSegments(matchStatement, source, depth, initialScope) ??
                    TryExpandRepeatedChain(matchStatement, source, depth) ??
                    TryExpandRepeatedBagShapes(matchStatement, source, depth, initialScope)) is { } expandedChain)
            {
                edits.Add((matchStatement.Start.StartIndex, matchStatement.Stop.StopIndex - matchStatement.Start.StartIndex + 1, expandedChain));
                continue;
            }
            if (node is GQLParser.PfQuantifiedPathPrimaryContext quantified &&
                quantified.pathPrimary() is GQLParser.PpParenthesizedPathPatternExpressionContext primary &&
                quantified.Parent is GQLParser.PathTermContext outerTerm && outerTerm.pathFactor().Length == 1)
            {
                var group = primary.parenthesizedPathPatternExpression();
                if (group.subpathVariableDeclaration() is null && group.pathModePrefix() is null &&
                    group.parenthesizedPathPatternWhereClause() is null &&
                    group.pathPatternExpression() is GQLParser.PpePathTermContext inner)
                {
                    var factors = inner.pathTerm().pathFactor();
                    if (factors.Length == 3 &&
                        factors[0] is GQLParser.PfPathPrimaryContext first && first.pathPrimary().GetText() == "()" &&
                        factors[2] is GQLParser.PfPathPrimaryContext last && last.pathPrimary().GetText() == "()" &&
                        factors[1] is GQLParser.PfPathPrimaryContext middle &&
                        middle.pathPrimary() is GQLParser.PpElementPatternContext element && element.elementPattern().edgePattern() is { } repeatedEdge)
                    {
                        // Anonymous, unconstrained boundary nodes contribute no
                        // group variables. Repeating this one-edge group is the
                        // existing quantified-edge operation, including its edge
                        // list binding and path-mode enforcement.
                        const string prefix = "MATCH ", suffix = " RETURN 1 AS __groupProbe";
                        string expanded = "()" + source[repeatedEdge.Start.StartIndex..(repeatedEdge.Stop.StopIndex + 1)] +
                            quantified.graphPatternQuantifier().GetText() + "()";
                        string prepared = Prepare(prefix + expanded + suffix, depth + 1);
                        edits.Add((quantified.Start.StartIndex, quantified.Stop.StopIndex - quantified.Start.StartIndex + 1,
                            prepared[prefix.Length..^suffix.Length]));
                        continue;
                    }
                }
            }
            if (node is GQLParser.ExistsPredicateContext exists && exists.LEFT_PAREN() is { } existsOpen)
            {
                edits.Add((existsOpen.Symbol.StartIndex, existsOpen.Symbol.Text.Length, "{"));
                var existsClose = exists.RIGHT_PAREN().Symbol;
                edits.Add((existsClose.StartIndex, existsClose.Text.Length, "}"));
            }
            if (node is GQLParser.ParenthesizedPathPatternExpressionContext groupedPath)
            {
                if (groupedPath.subpathVariableDeclaration() is not null || groupedPath.pathModePrefix() is not null ||
                    groupedPath.pathPatternExpression() is not GQLParser.PpePathTermContext)
                {
                    throw new ArgumentException("Subpath variables, modes and alternation in parenthesized paths are not yet supported.");
                }
                for (IParseTree? ancestor = groupedPath.Parent; ancestor is not null && ancestor is not GQLParser.GraphPatternContext; ancestor = ancestor.Parent)
                {
                    if (ancestor is GQLParser.PfQuantifiedPathPrimaryContext or GQLParser.PfQuestionedPathPrimaryContext)
                    {
                        throw new ArgumentException("Quantified or questioned parenthesized paths are not yet supported.");
                    }
                }
                edits.Add((groupedPath.Start.StartIndex, groupedPath.Start.Text.Length, ""));
                edits.Add((groupedPath.Stop.StartIndex, groupedPath.Stop.Text.Length, ""));
            }
            if (node is GQLParser.IsLabelExpressionContext label && label.Parent is GQLParser.ElementPatternFillerContext filler &&
                !IsSimpleLabelExpression(label.labelExpression(), filler.Parent is GQLParser.NodePatternContext))
            {
                string binding = filler.elementVariableDeclaration()?.GetText() ??
                    throw new ArgumentException("Complex label expressions currently require an element variable.");
                AddPatternPredicate(filler, binding + " IS LABELED (" + label.labelExpression().GetText() + ")");
                edits.Add((label.Start.StartIndex, label.Stop.StopIndex - label.Start.StartIndex + 1, ""));
                continue;
            }
            if (node is GQLParser.ParenthesizedPathPatternWhereClauseContext subpathWhere)
            {
                var condition = subpathWhere.searchCondition();
                const string prefix = "RETURN ", suffix = " AS __subpathPredicate";
                string prepared = PreparePatternPredicate(condition, prefix, suffix);
                AddPatternPredicate(subpathWhere, prepared[prefix.Length..^suffix.Length]);
                edits.Add((subpathWhere.Start.StartIndex, subpathWhere.Stop.StopIndex - subpathWhere.Start.StartIndex + 1, ""));
                continue;
            }
            if (node is GQLParser.ElementPatternWhereClauseContext elementWhere &&
                elementWhere.Parent.Parent is GQLParser.ElementPatternFillerContext predicateFiller)
            {
                var condition = elementWhere.searchCondition();
                const string prefix = "RETURN ", suffix = " AS __elementPredicate";
                string prepared = PreparePatternPredicate(condition, prefix, suffix);
                AddPatternPredicate(predicateFiller, prepared[prefix.Length..^suffix.Length]);
                edits.Add((elementWhere.Start.StartIndex, elementWhere.Stop.StopIndex - elementWhere.Start.StartIndex + 1, ""));
                continue;
            }
            if (node is GQLParser.FilterStatementContext filter && filter.whereClause() is { } filterWhere)
            {
                var keyword = filterWhere.WHERE().Symbol;
                edits.Add((keyword.StartIndex, keyword.StopIndex - keyword.StartIndex + 1, ""));
            }
            if (node is GQLParser.DeleteStatementContext deletion && deletion.NODETACH() is { } nodeDetach)
            {
                edits.Add((nodeDetach.Symbol.StartIndex, nodeDetach.Symbol.Text.Length, ""));
            }
            if (node is GQLParser.RecordConstructorContext record && record.RECORD() is { } recordKeyword)
            {
                edits.Add((recordKeyword.Symbol.StartIndex, recordKeyword.Symbol.Text.Length, ""));
            }
            if (node is GQLParser.ListValueConstructorByEnumerationContext list && list.listValueTypeName() is { } listKeyword)
            {
                edits.Add((listKeyword.Start.StartIndex, listKeyword.Stop.StopIndex - listKeyword.Start.StartIndex + 1, ""));
            }
            if (node is GQLParser.ExactNumericLiteralContext exact && exact.unsignedInteger() is null)
            {
                string literal = ExactDecimalLiteral(exact.GetText());
                edits.Add((exact.Start.StartIndex, exact.Stop.StopIndex - exact.Start.StartIndex + 1, literal));
            }
            if (node is GQLParser.UnsignedIntegerContext integer)
            {
                string text = NormalizeUnsignedInteger(integer.GetText());
                if (text != integer.GetText())
                {
                    edits.Add((integer.Start.StartIndex, integer.Stop.StopIndex - integer.Start.StartIndex + 1, text));
                }
            }
            if (node is GQLParser.IsOrColonContext labelIntroducer)
            {
                TightPunctuation(labelIntroducer.Start, labelIntroducer.Stop, ":");
            }
            if (node is GQLParser.LabelSetSpecificationContext labels)
            {
                foreach (var conjunction in labels.AMPERSAND())
                {
                    TightPunctuation(conjunction.Symbol, conjunction.Symbol, ":");
                }
            }
            if (node is GQLParser.ForOrdinalityOrOffsetContext position)
            {
                string mode = position.ORDINALITY() is not null ? "__ORDINALITY" : "__OFFSET";
                edits.Add((position.Start.StartIndex, position.Stop.StopIndex - position.Start.StartIndex + 1,
                    mode + " " + position.bindingVariable().GetText()));
            }
            // Typed literals and clock keywords have standard spellings distinct
            // from the constructor forms used by the execution compiler.
            if (node is GQLParser.DateLiteralContext date)
            {
                var literal = date.dateString();
                edits.Add((date.Start.StartIndex, date.Stop.StopIndex - date.Start.StartIndex + 1,
                    "DATE(" + source[literal.Start.StartIndex..(literal.Stop.StopIndex + 1)] + ")"));
            }
            else if (node is GQLParser.DurationLiteralContext duration)
            {
                var literal = duration.durationString();
                edits.Add((duration.Start.StartIndex, duration.Stop.StopIndex - duration.Start.StartIndex + 1,
                    "DURATION(" + source[literal.Start.StartIndex..(literal.Stop.StopIndex + 1)] + ")"));
            }
            else if (node is GQLParser.DateFunctionContext or GQLParser.TimeFunctionContext or
                GQLParser.DatetimeFunctionContext or GQLParser.LocaltimeFunctionContext or GQLParser.LocaldatetimeFunctionContext)
            {
                string? clock = node.GetText().ToUpperInvariant() switch
                {
                    "CURRENT_DATE" => "DATE()",
                    "CURRENT_TIME" => "ZONED_TIME()",
                    "CURRENT_TIMESTAMP" => "ZONED_DATETIME()",
                    "LOCAL_TIME" => "LOCAL_TIME()",
                    "LOCAL_TIMESTAMP" => "LOCAL_DATETIME()",
                    _ => null
                };
                if (clock is not null && node is ParserRuleContext context)
                {
                    edits.Add((context.Start.StartIndex, context.Stop.StopIndex - context.Start.StartIndex + 1, clock));
                }
            }
            if (node is GQLParser.AbbreviatedEdgePatternContext edge)
            {
                string expanded = edge.GetText() switch
                {
                    "->" => "-[]->",
                    "<-" => "<-[]-",
                    "-" => "-[]-",
                    "<->" => "-[]-",
                    _ => throw new ArgumentException("This abbreviated edge direction is not yet supported.")
                };
                edits.Add((edge.Start.StartIndex, edge.Stop.StopIndex - edge.Start.StartIndex + 1, expanded));
            }
            if (node is GQLParser.FullEdgeLeftOrRightContext eitherDirection)
            {
                // The property-graph store currently contains directed edges only.
                // Its direction-neutral matcher is therefore the union of left
                // and right directed matches; no undirected edge is admitted.
                edits.Add((eitherDirection.Start.StartIndex, eitherDirection.Start.Text.Length, "-["));
                edits.Add((eitherDirection.Stop.StartIndex, eitherDirection.Stop.Text.Length, "]-"));
            }
            if (node is GQLParser.ReturnItemContext item && item.returnItemAlias() is null &&
                item.aggregatingValueExpression().GetText().Equals("GRAPH", StringComparison.OrdinalIgnoreCase))
            {
                // GRAPH is a legal unreserved identifier, not a graph-export clause.
                edits.Add((item.Stop.StopIndex + 1, 0, " AS " + item.GetText()));
            }
            if (node is GQLParser.PrimitiveResultStatementContext result && result.GetText().Equals("FINISH", StringComparison.OrdinalIgnoreCase))
            {
                edits.Add((result.Start.StartIndex, result.Stop.StopIndex - result.Start.StartIndex + 1, "RETURN 1 AS __finished LIMIT 0"));
            }
            if (node is GQLParser.StatementBlockContext block)
            {
                var previous = block.statement();
                foreach (var next in block.nextStatement())
                {
                    var clauses = GraphGqlQueryCompiler.WithClauses(source[previous.Start.StartIndex..(previous.Stop.StopIndex + 1)]);
                    var returns = clauses.Where(clause => clause.Word == "RETURN").ToArray();
                    if (returns.Length != 1 || clauses.Any(clause => clause.Word == "UNION"))
                    {
                        throw new ArgumentException("NEXT currently requires one preceding linear RETURN statement.");
                    }
                    edits.Add((previous.Start.StartIndex + returns[0].Start, returns[0].Length, "WITH"));
                    if (next.yieldClause() is { } yield)
                    {
                        var items = yield.yieldItemList();
                        string projection = PrepareYieldProjection(items);
                        edits.Add((next.NEXT().Symbol.StartIndex,
                            yield.Stop.StopIndex - next.NEXT().Symbol.StartIndex + 1, "WITH " + projection));
                    }
                    else
                    {
                        edits.Add((next.NEXT().Symbol.StartIndex, next.NEXT().Symbol.Text.Length, ""));
                    }
                    previous = next.statement();
                }
            }
            for (int index = node.ChildCount - 1; index >= 0; index--)
            {
                pending.Push(node.GetChild(index));
            }
        }
        foreach (var (pattern, predicates) in patternPredicates)
        {
            string predicate = "(" + String.Join(") AND (", predicates) + ")";
            if (pattern.graphPatternWhereClause() is { } where)
            {
                var condition = where.searchCondition();
                edits.Add((condition.Start.StartIndex, 0, "("));
                edits.Add((condition.Stop.StopIndex + 1, 0, ") AND " + predicate));
            }
            else
            {
                edits.Add((pattern.Stop.StopIndex + 1, 0, " WHERE " + predicate));
            }
        }
        if (source.Length + edits.Sum(edit => edit.Text.Length - edit.Length) > GraphGqlQueryCompiler.MaximumQueryCharacters)
        {
            throw new ArgumentException("Expanded GQL source exceeds the query character limit.");
        }
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            source = source.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }
        return source;
    }


    private static string PrepareYieldProjection(GQLParser.YieldItemListContext items)
        => String.Join(',', items.yieldItem().Select(item =>
        {
            string spelling = item.yieldItemName().GetText();
            string name = GraphGqlIdentifiers.DecodeName(GraphGqlQueryCompiler.NormalizeQuotedStrings(spelling));
            string output = item.yieldItemAlias()?.bindingVariable().GetText() ?? name;
            return spelling.StartsWith('"')
                ? "__YIELD_COLUMN(" + System.Text.Json.JsonSerializer.Serialize(name) + ") AS " + GraphGqlIdentifiers.QuoteAlias(output)
                : name + " AS " + output;
        }));

    private static bool IsSimpleLabelExpression(GQLParser.LabelExpressionContext expression, bool allowConjunction)
        => expression is GQLParser.LabelExpressionNameContext ||
            allowConjunction && expression is GQLParser.LabelExpressionConjunctionContext conjunction &&
            conjunction.labelExpression().All(part => IsSimpleLabelExpression(part, true));

    private static string NormalizeUnsignedInteger(string source)
    {
        string text = source.Replace("_", "", StringComparison.Ordinal);
        int radix = text.StartsWith("0x", StringComparison.Ordinal) ? 16 :
            text.StartsWith("0o", StringComparison.Ordinal) ? 8 : text.StartsWith("0b", StringComparison.Ordinal) ? 2 : 10;
        if (radix != 10)
        {
            ulong value = 0;
            try
            {
                foreach (char digit in text.AsSpan(2))
                {
                    int number = digit <= '9' ? digit - '0' : Char.ToUpperInvariant(digit) - 'A' + 10;
                    value = checked(value * (ulong)radix + (ulong)number);
                }
            }
            catch (OverflowException error)
            {
                throw new ArgumentException("Integer literal exceeds the supported 64-bit range.", error);
            }
            text = value.ToString(CultureInfo.InvariantCulture);
        }
        return text;
    }

    private static string ExactDecimalLiteral(string source)
    {
        string value = source.Replace("_", "", StringComparison.Ordinal).TrimEnd('m', 'M');
        int exponentAt = value.IndexOf('e', StringComparison.OrdinalIgnoreCase);
        int exponent = 0;
        if (exponentAt >= 0)
        {
            if (!Int32.TryParse(value.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent) ||
                exponent is < -4096 or > 4096)
            {
                throw new ArgumentException("Exact decimal exponent exceeds the supported expansion limit.");
            }
            value = value[..exponentAt];
        }
        int dot = value.IndexOf('.');
        int point = (dot < 0 ? value.Length : dot) + exponent;
        string digits = value.Replace(".", "", StringComparison.Ordinal);
        return point <= 0 ? "0." + new string('0', -point) + digits :
            point >= digits.Length ? digits + new string('0', point - digits.Length) + ".0" : digits.Insert(point, ".");
    }

    private static ArgumentException InvalidSyntax(string message, Exception? inner = null)
    {
        var error = new ArgumentException(message, inner);
        error.Data["EntityForge.GqlStatus"] = "42001";
        return error;
    }

}
