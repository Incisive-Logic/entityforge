using System.Numerics;
using System.Runtime.CompilerServices;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

public static partial class RdfSparql
{
    // Native candidate sets are immutable and shared by unchanged predicate columns.
    // Weak keys release indexes with their snapshot; noninteger evidence is never pruned.
    private static readonly ConditionalWeakTable<IReadOnlySet<RdfQuad>, IntegerCandidates> IntegerIndexes = new();
    private const string IntegerDatatype = "http://www.w3.org/2001/XMLSchema#integer";

    private static bool ExactInteger(RdfTerm? term, out BigInteger value)
    {
        value = default;
        return term is RdfLiteral { Datatype: IntegerDatatype } literal &&
            TryParseExactDecimal(literal, out value, out int scale) && scale == 0;
    }

    private static IEnumerable<RdfQuad> SelectIntegerCandidates(IReadOnlySet<RdfQuad> source,
        Pattern pattern, ComparisonExpression[]? filters, Action cancel)
    {
        if (pattern.Object.Variable is not { } variable || filters is null)
        {
            return source;
        }

        var bounds = filters.Where(f => f.Left is TermValueExpression { Term.Variable: { } name } && name == variable &&
            f.Right is TermValueExpression { Term.Constant: { } term } && ExactInteger(term, out _) &&
            f.Operator is "=" or "<" or "<=" or ">" or ">=").ToArray();
        if (bounds.Length == 0)
        {
            return source;
        }

        var index = IntegerIndexes.GetValue(source, static _ => new());
        return index.Select(source, bounds.Select(f =>
        {
            ExactInteger(((TermValueExpression)f.Right).Term.Constant, out var value);
            return (f.Operator, value);
        }).ToArray(), cancel);
    }

    internal sealed class IntegerCandidates
    {
        private readonly object gate = new();
        private (BigInteger Value, RdfQuad Quad)[]? ordered;
        private RdfQuad[] fallback = [];

        internal IEnumerable<RdfQuad> Select(IReadOnlySet<RdfQuad> source,
            IReadOnlyList<(string Operator, BigInteger Value)> bounds, Action cancel)
        {
            lock (gate)
            {
                cancel();
                if (ordered is null)
                {
                    var values = new List<(BigInteger Value, RdfQuad Quad)>();
                    var other = new List<RdfQuad>();
                    foreach (var quad in source)
                    {
                        cancel();
                        if (ExactInteger(quad.Object, out var value))
                        {
                            values.Add((value, quad));
                        }
                        else
                        {
                            other.Add(quad);
                        }
                    }
                    values.Sort(static (a, b) => a.Value.CompareTo(b.Value));
                    cancel();
                    fallback = other.ToArray();
                    ordered = values.ToArray();
                }
            }
            int first = 0, last = ordered.Length;
            foreach (var (op, value) in bounds)
            {
                cancel();
                if (op is "=" or ">" or ">=")
                {
                    first = Math.Max(first, Boundary(value, op == ">"));
                }

                if (op is "=" or "<" or "<=")
                {
                    last = Math.Min(last, Boundary(value, op != "<"));
                }
            }
            for (int i = first; i < last; i++)
            {
                cancel();
                yield return ordered[i].Quad;
            }
            foreach (var quad in fallback)
            {
                cancel();
                yield return quad;
            }
        }

        private int Boundary(BigInteger value, bool afterEqual)
        {
            int low = 0, high = ordered!.Length;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                int comparison = ordered[middle].Value.CompareTo(value);
                if (comparison < 0 || afterEqual && comparison == 0)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }
    }
}
