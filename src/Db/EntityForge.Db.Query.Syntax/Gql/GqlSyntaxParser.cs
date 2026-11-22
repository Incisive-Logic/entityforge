using Antlr4.Runtime;
using Antlr4.Runtime.Atn;
using Antlr4.Runtime.Dfa;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;

namespace EntityForge.Db.Query.Syntax.Gql;

// Syntax and source positions only. This layer never accesses storage, HTTP,
// graph identities, semantic provenance or profile-specific execution services.
internal static class GqlSyntaxParser
{
    internal const int MaximumQueryCharacters = 16_384;

    internal static GQLParser.GqlProgramContext Parse(string source, out CommonTokenStream tokens)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumQueryCharacters)
        {
            throw new ArgumentException("GQL source exceeds the query character limit.");
        }
        var lexer = new GQLLexer(CharStreams.fromString(source));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(new LexerErrors());
        tokens = new BoundedTokens(lexer);
        tokens.Fill();
        if (tokens.Size > 4096)
        {
            throw new ArgumentException("GQL syntax exceeds 4,096 tokens.");
        }
        // ANTLR's Unicode stream indexes scalars; all source rewriting below
        // uses .NET UTF-16 offsets. Preserve token spelling before translating
        // the spans, including tokens after supplementary-plane identifiers.
        var offsets = new List<int> { 0 };
        int utf16 = 0;
        foreach (var rune in source.EnumerateRunes())
        {
            utf16 += rune.Utf16SequenceLength;
            offsets.Add(utf16);
        }
        foreach (CommonToken token in tokens.GetTokens())
        {
            string spelling = token.Text;
            token.StartIndex = offsets[token.StartIndex];
            token.StopIndex = offsets[token.StopIndex + 1] - 1;
            token.Text = spelling;
        }
        int depth = 0;
        foreach (var token in tokens.GetTokens())
        {
            if (token.Channel != TokenConstants.DefaultChannel)
            {
                continue;
            }
            if (token.Text is "(" or "[" or "{" || token.Type == GQLLexer.CASE)
            {
                if (++depth > 64)
                {
                    throw new ArgumentException("GQL syntax exceeds 64 nesting levels.");
                }
            }
            else if (token.Text is ")" or "]" or "}" || token.Type == GQLLexer.END)
            {
                depth--;
            }
        }
        tokens.Seek(0);
        // Validation includes structural restrictions below, so building the
        // tree is required even when the caller only needs a syntax check.
        var parser = new BoundedParser(tokens) { BuildParseTree = true, ErrorHandler = new BailErrorStrategy() };
        parser.RemoveErrorListeners();
        try
        {
            var root = parser.gqlProgram();
            // OpenGQL flattens the standard's expression productions to avoid
            // mutual left recursion. Restore the restriction that a comparison
            // predicand cannot itself be an unparenthesized comparison.
            var pending = new Stack<IParseTree>();
            pending.Push(root);
            while (pending.TryPop(out var node))
            {
                if (node is GQLParser.CompositeQueryExpressionContext composite &&
                    composite.Parent is not GQLParser.CompositeQueryExpressionContext)
                {
                    string? expected = null;
                    for (var current = composite; current is not null; current = current.compositeQueryExpression())
                    {
                        if (current.queryConjunction() is not { } conjunction)
                        {
                            continue;
                        }
                        if (conjunction.setOperator() is not { } set)
                        {
                            continue;
                        }
                        string actual = set.GetChild(0).GetText().ToUpperInvariant() + ":" +
                            (set.setQuantifier()?.GetText().ToUpperInvariant() ?? "DISTINCT");
                        if (expected is not null && expected != actual)
                        {
                            throw InvalidSyntax("A composite query must use the same set operator and ALL or DISTINCT qualifier throughout; use separate subqueries or NEXT stages to combine them.");
                        }
                        expected = actual;
                    }
                }
                if (node is GQLParser.ComparisonExprAltContext comparison &&
                    comparison.valueExpression().Any(operand => operand is GQLParser.ComparisonExprAltContext or
                        GQLParser.NotExprAltContext or GQLParser.IsNotExprAltContext or GQLParser.ConjunctiveExprAltContext or
                        GQLParser.DisjunctiveExprAltContext or GQLParser.PredicateExprAltContext or GQLParser.NormalizedPredicateExprAltContext))
                {
                    throw InvalidSyntax("A predicate used as a comparison operand requires parentheses; chained comparisons are not GQL syntax.");
                }
                if (node is GQLParser.NotExprAltContext not && not.valueExpression() is GQLParser.NotExprAltContext ||
                    node is GQLParser.IsNotExprAltContext truth && truth.valueExpression() is GQLParser.IsNotExprAltContext ||
                    node is GQLParser.SignedExprAltContext signed && signed.valueExpression() is GQLParser.SignedExprAltContext)
                {
                    throw InvalidSyntax("Repeated unary signs, NOT or truth tests require parentheses.");
                }
                for (int index = node.ChildCount - 1; index >= 0; index--)
                {
                    pending.Push(node.GetChild(index));
                }
            }
            return root;
        }
        catch (ParseCanceledException error)
        {
            var token = parser.CurrentToken;
            throw InvalidSyntax($"Invalid GQL syntax at line {token.Line}, column {token.Column + 1}.", error);
        }
    }

    private static ArgumentException InvalidSyntax(string message, Exception? inner = null)
    {
        var error = new ArgumentException(message, inner);
        error.Data["EntityForge.GqlStatus"] = "42001";
        return error;
    }

    private sealed class BoundedParser : GQLParser
    {
        private int _ruleDepth;
        private readonly Dictionary<int, int> _recursiveDepths = [];
        private readonly Stack<int> _recursiveRules = [];

        public BoundedParser(ITokenStream input) : base(input)
        {
            Interpreter = new BoundedPrediction(this, Atn, decisionToDFA, sharedContextCache);
        }

        private void EnterBoundedRule()
        {
            // Grammar wrappers mean one source nesting level can enter several
            // rules. Retain the documented bracket/CASE boundary while bounding
            // recursive productions that have no bracket tokens (including types).
            if (++_ruleDepth > 1024)
            {
                throw new ArgumentException("GQL syntax exceeds its recursive parser work budget.");
            }
        }

        public override void EnterRule(ParserRuleContext localContext, int state, int ruleIndex)
        {
            EnterBoundedRule();
            base.EnterRule(localContext, state, ruleIndex);
        }

        public override void ExitRule()
        {
            try
            {
                base.ExitRule();
            }
            finally
            {
                _ruleDepth--;
            }
        }

        public override void EnterRecursionRule(ParserRuleContext localContext, int state, int ruleIndex, int precedence)
        {
            int depth = _recursiveDepths.GetValueOrDefault(ruleIndex);
            if (depth >= 128 || _recursiveRules.Count >= 256)
            {
                throw new ArgumentException("GQL syntax exceeds its recursive parser work budget.");
            }
            _recursiveDepths[ruleIndex] = depth + 1;
            _recursiveRules.Push(ruleIndex);
            EnterBoundedRule();
            base.EnterRecursionRule(localContext, state, ruleIndex, precedence);
        }

        public override void UnrollRecursionContexts(ParserRuleContext parentContext)
        {
            try
            {
                base.UnrollRecursionContexts(parentContext);
            }
            finally
            {
                int ruleIndex = _recursiveRules.Pop();
                _recursiveDepths[ruleIndex]--;
                _ruleDepth--;
            }
        }
    }

    private sealed class BoundedPrediction(Parser parser, ATN atn, DFA[] decisions, PredictionContextCache cache)
        : ParserATNSimulator(parser, atn, decisions, cache)
    {
        private readonly Dictionary<PredictionContext, PredictionBudget> _checkedContexts = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PredictionContext, int> _shallowContexts = new(ReferenceEqualityComparer.Instance);
        private readonly Stack<(PredictionContext Context, bool Expanded)> _pending = new();

        protected override ATNConfigSet ComputeReachSet(ATNConfigSet closure, int tokenType, bool fullContext)
        {
            CheckDepth(closure);
            var reachable = base.ComputeReachSet(closure, tokenType, fullContext);
            CheckDepth(reachable);
            return reachable;
        }

        private void CheckDepth(ATNConfigSet? configurations)
        {
            if (configurations is null)
            {
                return;
            }
            // A path containing at most 128 contexts cannot repeat any one
            // rule more than 128 times. Most ordinary queries need only this
            // inexpensive proof; detailed rule summaries are for deeper DAGs.
            if (CheckShallowContexts(configurations))
            {
                return;
            }
            // Track maximum occurrences of each rule along each context path.
            // A CASE adds several different grammar wrappers, whereas repeated
            // unary operators repeatedly enter the same rule. Merge per-rule
            // maxima from shared parents to avoid enumerating every DAG path.
            // Contexts are immutable. Reuse summaries only within this parse;
            // clear the bounded memo between checks rather than retaining it
            // across queries or repeatedly allocating summaries for shared DAGs.
            if (_checkedContexts.Count >= 4096)
            {
                _checkedContexts.Clear();
            }
            var seen = _checkedContexts;
            var pending = _pending;
            pending.Clear();
            foreach (var configuration in configurations.Elements)
            {
                if (configuration.context is { } context)
                {
                    pending.Push((context, false));
                }
            }
            int work = 0;
            while (pending.TryPop(out var entry))
            {
                var context = entry.Context;
                if (seen.ContainsKey(context))
                {
                    continue;
                }
                if (!entry.Expanded)
                {
                    pending.Push((context, true));
                    for (int index = 0; index < context.Size; index++)
                    {
                        if (context.GetParent(index) is { } parent && !seen.ContainsKey(parent))
                        {
                            pending.Push((parent, false));
                        }
                    }
                    continue;
                }
                var current = new PredictionBudget();
                for (int index = 0; index < context.Size; index++)
                {
                    if (context.GetParent(index) is not { } parent)
                    {
                        continue;
                    }
                    var source = seen[parent];
                    if (++work > 100_000 || source.Depth >= 1024)
                    {
                        throw new ArgumentException("GQL syntax exceeds its recursive parser work budget.");
                    }
                    int rule = this.atn.states[context.GetReturnState(index)].ruleIndex;
                    current.Depth = Math.Max(current.Depth, source.Depth + 1);
                    foreach (var pair in source.Rules)
                    {
                        int count = pair.Value + (pair.Key == rule ? 1 : 0);
                        if (++work > 100_000 || count > 128)
                        {
                            throw new ArgumentException("GQL syntax exceeds its recursive parser work budget.");
                        }
                        if (count > current.Rules.GetValueOrDefault(pair.Key))
                        {
                            current.Rules[pair.Key] = count;
                        }
                    }
                    if (!current.Rules.ContainsKey(rule))
                    {
                        current.Rules[rule] = 1;
                    }
                }
                seen.Add(context, current);
            }
        }

        private bool CheckShallowContexts(ATNConfigSet configurations)
        {
            if (_shallowContexts.Count >= 4096)
            {
                _shallowContexts.Clear();
            }
            var pending = _pending;
            pending.Clear();
            foreach (var configuration in configurations.Elements)
            {
                if (configuration.context is { } context && !_shallowContexts.ContainsKey(context))
                {
                    pending.Push((context, false));
                }
            }
            if (pending.Count == 0)
            {
                return true;
            }
            int work = 0;
            while (pending.TryPop(out var entry))
            {
                if (_shallowContexts.ContainsKey(entry.Context))
                {
                    continue;
                }
                if (++work > 100_000)
                {
                    throw new ArgumentException("GQL syntax exceeds its recursive parser work budget.");
                }
                if (!entry.Expanded)
                {
                    pending.Push((entry.Context, true));
                    for (int index = 0; index < entry.Context.Size; index++)
                    {
                        if (entry.Context.GetParent(index) is { } parent && !_shallowContexts.ContainsKey(parent))
                        {
                            pending.Push((parent, false));
                        }
                    }
                    continue;
                }
                int depth = 0;
                for (int index = 0; index < entry.Context.Size; index++)
                {
                    if (entry.Context.GetParent(index) is { } parent)
                    {
                        depth = Math.Max(depth, _shallowContexts[parent] + 1);
                    }
                }
                if (depth > 128)
                {
                    return false;
                }
                _shallowContexts.Add(entry.Context, depth);
            }
            return true;
        }

        private sealed class PredictionBudget
        {
            public int Depth
            {
                get; set;
            }

            public Dictionary<int, int> Rules { get; } = [];
        }
    }

    private sealed class BoundedTokens(ITokenSource source) : CommonTokenStream(source)
    {
        private int _lookups;

        public override IToken LT(int k)
        {
            if (++_lookups > 1_000_000)
            {
                throw new ArgumentException("GQL syntax exceeds its parser work budget.");
            }
            return base.LT(k);
        }
    }

    private sealed class LexerErrors : IAntlrErrorListener<int>
    {
        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line,
            int charPositionInLine, string msg, RecognitionException e)
            => throw InvalidSyntax($"Invalid GQL token at line {line}, column {charPositionInLine + 1}.");
    }
}
