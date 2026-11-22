using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    private static List<Dictionary<string, RdfTerm>> MatchPath(RdfSparqlReadView database, Pattern pattern,
        IReadOnlyList<Dictionary<string, RdfTerm>> rows)
    {
        var results = new List<Dictionary<string, RdfTerm>>();
        var budget = new PathWorkBudget(database.Cancellation);
        foreach (Dictionary<string, RdfTerm> row in rows)
        {
            RdfTerm? graph = pattern.Graph is null ? null : Resolve(pattern.Graph, row);
            if (graph is RdfLiteral or RdfTripleTerm)
            {
                continue;
            }
            IEnumerable<RdfTerm?> graphs = pattern.Graph is not null && graph is null
                ? PathGraphs(database, budget).Select(item => (RdfTerm?)item) : [graph];
            foreach (RdfTerm? activeGraph in graphs)
            {
                if (activeGraph is not null)
                {
                    budget.Spend();
                    if (database.ReadPrefix(new(Graph: activeGraph), 1).Count == 0)
                    {
                        continue;
                    }
                }
                var graphRow = new Dictionary<string, RdfTerm>(row, StringComparer.Ordinal);
                if (pattern.Graph is not null && !Bind(pattern.Graph, activeGraph!, graphRow))
                {
                    continue;
                }
                RdfTerm? subject = Resolve(pattern.Subject, graphRow);
                RdfTerm? obj = Resolve(pattern.Object, graphRow);
                bool reverse = subject is null && obj is not null;
                IEnumerable<RdfTerm> starts = subject is not null ? [subject] : obj is not null ? [obj] : PathNodes(database, activeGraph, budget);
                foreach (RdfTerm start in starts)
                {
                    if (pattern.Subject.Variable is not null && pattern.Object.Variable is not null &&
                        (subject is not null || obj is not null) && !PathNodeExists(database, activeGraph, start, budget))
                    {
                        continue;
                    }
                    foreach (RdfTerm endpoint in Traverse(database, pattern.Path!, start, activeGraph, reverse, budget))
                    {
                        if (!reverse && obj is not null && !Equals(endpoint, obj))
                        {
                            continue;
                        }
                        var joined = new Dictionary<string, RdfTerm>(graphRow, StringComparer.Ordinal);
                        if (Bind(pattern.Subject, reverse ? endpoint : start, joined) &&
                            Bind(pattern.Object, reverse ? start : endpoint, joined))
                        {
                            AddBounded(results, joined);
                        }
                    }
                }
            }
        }
        return results;
    }

    private static IReadOnlyList<RdfTerm> Traverse(RdfSparqlReadView database, RdfPathExpression path,
        RdfTerm start, RdfTerm? graph, bool reverse, PathWorkBudget budget)
    {
        IEnumerable<RdfTerm> result = path switch
        {
            RdfPredicatePath predicate => TraversePredicate(database, predicate.Predicate, start, graph,
                reverse ^ predicate.Inverse, budget),
            RdfNegatedPath negated => TraverseNegated(database, negated, start, graph, reverse, budget),
            RdfSequencePath sequence => TraverseSequence(database,
                reverse ? sequence.Elements.Reverse().ToArray() : sequence.Elements, start, graph, reverse, budget),
            RdfAlternativePath alternative => alternative.Alternatives.SelectMany(item =>
                Traverse(database, item, start, graph, reverse, budget)),
            RdfRepeatedPath repeated => TraverseRepeated(database, repeated, start, graph, reverse, budget),
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };
        return BoundedEndpoints(result);
    }

    private static RdfTerm[] BoundedEndpoints(IEnumerable<RdfTerm> result)
    {
        // Sequences and alternatives preserve solution multiplicity. Only
        // repetition operators eliminate duplicate reachable endpoints.
        RdfTerm[] distinct = result.Take(MaximumResults + 1).ToArray();
        if (distinct.Length > MaximumResults)
        {
            throw new RdfSparqlWorkLimitException($"SPARQL property-path evaluation exceeded {MaximumResults} endpoints.");
        }

        return distinct;
    }

    private static IEnumerable<RdfTerm> TraversePredicate(RdfSparqlReadView database, RdfIri predicate,
        RdfTerm start, RdfTerm? graph, bool reverse, PathWorkBudget budget)
    {
        budget.Spend();
        if (!reverse && start is RdfLiteral)
        {
            yield break;
        }
        var pattern = reverse
            ? new RdfQuadPattern(Predicate: predicate, Object: start, Graph: graph,
                MatchDefaultGraphOnly: graph is null)
            : new RdfQuadPattern(Subject: start, Predicate: predicate, Graph: graph,
                MatchDefaultGraphOnly: graph is null);
        foreach (var quad in database.ReadPrefix(pattern, budget.ReadLimit))
        {
            budget.Spend();
            yield return reverse ? quad.Subject : quad.Object;
        }
    }

    private static IEnumerable<RdfTerm> TraverseSequence(RdfSparqlReadView database,
        IReadOnlyList<RdfPathExpression> sequence, RdfTerm start, RdfTerm? graph, bool reverse, PathWorkBudget budget)
    {
        IReadOnlyList<RdfTerm> frontier = [start];
        // Bound each intermediate frontier: a later hop can shrink an oversized expansion.
        foreach (RdfPathExpression element in sequence)
        {
            frontier = BoundedEndpoints(frontier.SelectMany(item => Traverse(database, element, item, graph, reverse, budget)));
        }

        return frontier;
    }

    private static IEnumerable<RdfTerm> TraverseNegated(RdfSparqlReadView database, RdfNegatedPath path,
        RdfTerm start, RdfTerm? graph, bool reverse, PathWorkBudget budget)
    {
        budget.Spend();
        bool incoming = reverse ^ path.Inverse;
        if (!incoming && start is RdfLiteral)
        {
            yield break;
        }
        var pattern = incoming ? new RdfQuadPattern(Object: start, Graph: graph, MatchDefaultGraphOnly: graph is null) :
            new RdfQuadPattern(Subject: start, Graph: graph, MatchDefaultGraphOnly: graph is null);
        var endpoints = new HashSet<RdfTerm>();
        foreach (var quad in database.ReadPrefix(pattern, budget.ReadLimit))
        {
            budget.Spend();
            if (!path.Excluded.Contains(quad.Predicate) && endpoints.Add(incoming ? quad.Subject : quad.Object))
            {
                yield return incoming ? quad.Subject : quad.Object;
            }
        }
    }

    private static IEnumerable<RdfTerm> TraverseRepeated(RdfSparqlReadView database, RdfRepeatedPath repeated,
        RdfTerm start, RdfTerm? graph, bool reverse, PathWorkBudget budget)
    {
        var results = new HashSet<RdfTerm>();
        IReadOnlyList<RdfTerm> frontier = [start];
        if (repeated.Minimum == 0)
        {
            results.Add(start);
        }
        if (repeated.Maximum < 0)
        {
            var expanded = new HashSet<RdfTerm>();
            var pending = new Queue<RdfTerm>();
            pending.Enqueue(start);
            expanded.Add(start);
            while (pending.TryDequeue(out RdfTerm? current))
            {
                foreach (var endpoint in Traverse(database, repeated.Operand, current, graph, reverse, budget))
                {
                    results.Add(endpoint);
                    if (results.Count > MaximumResults)
                    {
                        throw new RdfSparqlWorkLimitException("SPARQL reachability exceeds the endpoint bound.");
                    }
                    if (expanded.Add(endpoint))
                    {
                        pending.Enqueue(endpoint);
                    }
                }
            }
            return results;
        }

        for (int depth = 1; depth <= repeated.Maximum; depth++)
        {
            frontier = BoundedEndpoints(frontier.SelectMany(item => Traverse(database, repeated.Operand, item, graph, reverse, budget)));
            if (frontier.Count == 0)
            {
                break;
            }

            if (depth >= repeated.Minimum)
            {
                foreach (RdfTerm endpoint in frontier)
                {
                    results.Add(endpoint);
                }
            }

            if (results.Count > MaximumResults)
            {
                throw new RdfSparqlWorkLimitException(
                    $"SPARQL property-path evaluation exceeded {MaximumResults} endpoints.");
            }
        }
        return results;
    }

    private static RdfPathExpression ParsePath(string[] tokens, ref int index)
    {
        RdfPathExpression path = ParseAlternativePath(tokens, ref index);
        if (PathSteps(path) > MaximumPathSteps)
        {
            throw new RdfSparqlWorkLimitException(
                $"A SPARQL property path cannot contain more than {MaximumPathSteps} predicate steps.");
        }

        return path;
    }

    private static RdfPathExpression ParseAlternativePath(string[] tokens, ref int index)
    {
        var alternatives = new List<RdfPathExpression> { ParseSequencePath(tokens, ref index) };
        while (index < tokens.Length && tokens[index] == "|")
        {
            index++;
            alternatives.Add(ParseSequencePath(tokens, ref index));
        }
        return alternatives.Count == 1 ? alternatives[0] : new RdfAlternativePath(alternatives);
    }

    private static RdfPathExpression ParseSequencePath(string[] tokens, ref int index)
    {
        var sequence = new List<RdfPathExpression> { ParseUnaryPath(tokens, ref index) };
        while (index < tokens.Length && tokens[index] == "/")
        {
            index++;
            sequence.Add(ParseUnaryPath(tokens, ref index));
        }
        return sequence.Count == 1 ? sequence[0] : new RdfSequencePath(sequence);
    }

    private static RdfPathExpression ParseUnaryPath(string[] tokens, ref int index)
    {
        bool inverse = index < tokens.Length && tokens[index] == "^";
        if (inverse)
        {
            index++;
        }

        RdfPathExpression path;
        if (index < tokens.Length && tokens[index] == "!")
        {
            index++;
            bool grouped = index < tokens.Length && tokens[index] == "(";
            if (grouped)
            {
                index++;
            }
            var forward = new List<RdfIri>();
            var backward = new List<RdfIri>();
            if (!grouped || index >= tokens.Length || tokens[index] != ")")
            {
                while (true)
                {
                    bool backwards = index < tokens.Length && tokens[index] == "^";
                    if (backwards)
                    {
                        index++;
                    }
                    TermPattern term = ParsePatternTerm(tokens, ref index, allowPredicateShorthand: true);
                    if (term.Constant is not RdfIri excluded)
                    {
                        throw new InvalidDataException("Negated property sets require predicate IRIs.");
                    }
                    (backwards ? backward : forward).Add(excluded);
                    if (!grouped || index >= tokens.Length || tokens[index] != "|")
                    {
                        break;
                    }
                    index++;
                }
            }
            if (grouped)
            {
                RequireToken(tokens, ref index, ")");
            }
            path = forward.Count > 0 && backward.Count > 0
                ? new RdfAlternativePath([new RdfNegatedPath(forward), new RdfNegatedPath(backward, true)])
                : backward.Count > 0 ? new RdfNegatedPath(backward, true) : new RdfNegatedPath(forward);
        }
        else if (index < tokens.Length && tokens[index] == "(")
        {
            index++;
            path = ParseAlternativePath(tokens, ref index);
            if (index >= tokens.Length || tokens[index++] != ")")
            {
                throw new InvalidDataException("A parenthesized property path must be closed.");
            }
        }
        else
        {
            TermPattern term = ParsePatternTerm(tokens, ref index, allowPredicateShorthand: true);
            if (term.Constant is not RdfIri predicate)
            {
                throw new NotSupportedException("A bounded property path step must be a concrete predicate IRI.");
            }

            path = new RdfPredicatePath(predicate, inverse);
            inverse = false;
        }
        if (inverse)
        {
            path = Invert(path);
        }

        if (index < tokens.Length)
        {
            path = tokens[index] switch
            {
                "?" => ConsumeRepeated(path, 0, 1, ref index),
                "*" => ConsumeRepeated(path, 0, -1, ref index),
                "+" => ConsumeRepeated(path, 1, -1, ref index),
                _ => path
            };
        }

        return path;
    }

    private static RdfPathExpression ConsumeRepeated(RdfPathExpression path, int minimum, int maximum,
        ref int index)
    {
        index++;
        return new RdfRepeatedPath(path, minimum, maximum);
    }

    private static RdfPathExpression Invert(RdfPathExpression path) => path switch
    {
        RdfPredicatePath predicate => predicate with { Inverse = !predicate.Inverse },
        RdfNegatedPath negated => negated with { Inverse = !negated.Inverse },
        RdfSequencePath sequence => new RdfSequencePath(sequence.Elements.Reverse().Select(Invert).ToArray()),
        RdfAlternativePath alternative => new RdfAlternativePath(alternative.Alternatives.Select(Invert).ToArray()),
        RdfRepeatedPath repeated => repeated with { Operand = Invert(repeated.Operand) },
        _ => throw new ArgumentOutOfRangeException(nameof(path))
    };

    private static int PathSteps(RdfPathExpression path) => path switch
    {
        RdfPredicatePath => 1,
        RdfNegatedPath negated => Math.Max(1, negated.Excluded.Count),
        RdfSequencePath sequence => sequence.Elements.Sum(PathSteps),
        RdfAlternativePath alternative => alternative.Alternatives.Sum(PathSteps),
        RdfRepeatedPath repeated => PathSteps(repeated.Operand),
        _ => throw new ArgumentOutOfRangeException(nameof(path))
    };

    private static bool IsPathOperator(string token) => token is "/" or "|" or "?" or "*" or "+";

    private abstract record RdfPathExpression;

    private sealed record RdfPredicatePath(RdfIri Predicate, bool Inverse = false) : RdfPathExpression;

    private sealed record RdfNegatedPath(IReadOnlyList<RdfIri> Excluded, bool Inverse = false) : RdfPathExpression;

    private sealed record RdfSequencePath(IReadOnlyList<RdfPathExpression> Elements) : RdfPathExpression;

    private sealed record RdfAlternativePath(IReadOnlyList<RdfPathExpression> Alternatives) : RdfPathExpression;

    private sealed record RdfRepeatedPath(RdfPathExpression Operand, int Minimum, int Maximum) : RdfPathExpression;

    private static bool PathNodeExists(RdfSparqlReadView database, RdfTerm? graph, RdfTerm node, PathWorkBudget budget)
    {
        if (node is not RdfLiteral)
        {
            budget.Spend();
            if (database.ReadPrefix(new(Subject: node, Graph: graph, MatchDefaultGraphOnly: graph is null), 1).Count != 0)
            {
                return true;
            }
        }
        budget.Spend();
        return database.ReadPrefix(new(Object: node, Graph: graph, MatchDefaultGraphOnly: graph is null), 1).Count != 0;
    }

    private static IEnumerable<RdfTerm> PathNodes(RdfSparqlReadView database, RdfTerm? graph, PathWorkBudget budget)
    {
        var nodes = new HashSet<RdfTerm>();
        foreach (var quad in database.ReadPrefix(new(Graph: graph, MatchDefaultGraphOnly: graph is null), budget.ReadLimit))
        {
            budget.Spend();
            nodes.Add(quad.Subject);
            nodes.Add(quad.Object);
            if (nodes.Count > MaximumResults)
            {
                throw new RdfSparqlWorkLimitException("SPARQL path start nodes exceed the endpoint bound.");
            }
        }
        return nodes;
    }

    private static IEnumerable<RdfTerm> PathGraphs(RdfSparqlReadView database, PathWorkBudget budget)
    {
        int count = 0;
        foreach (var graph in database.ReadNamedGraphNames(Math.Min(budget.ReadLimit, MaximumResults + 1)))
        {
            budget.Spend();
            if (++count > MaximumResults)
            {
                throw new RdfSparqlWorkLimitException("SPARQL path graph names exceed the endpoint bound.");
            }
            yield return graph;
        }
    }

    private sealed class PathWorkBudget(CancellationToken cancellation)
    {
        private int _work;

        // Request one beyond the remaining work so excess never becomes a
        // successful prefix. The adapter bounds allocation before returning.
        public int ReadLimit => MaximumPathExpansions - _work + 1;

        public void Spend()
        {
            cancellation.ThrowIfCancellationRequested();
            if (++_work > MaximumPathExpansions)
            {
                throw new RdfSparqlWorkLimitException($"SPARQL property paths exceed {MaximumPathExpansions} traversal operations.");
            }
        }
    }
}
