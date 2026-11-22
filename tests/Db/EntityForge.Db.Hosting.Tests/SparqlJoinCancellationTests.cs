using System.Collections;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SparqlJoinCancellationTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Sparql_join_candidate_cancellation_disposes_input_and_preserves_token(bool minus, bool onCompletion)
    {
        using var cancellation = new CancellationTokenSource();
        var candidates = new CancellingCandidates(cancellation, onCompletion);
        var error = Assert.ThrowsExactly<OperationCanceledException>(() => Execute(minus, candidates, cancellation.Token));
        Assert.AreEqual(cancellation.Token, error.CancellationToken);
        Assert.IsTrue(candidates.Disposed);
        Assert.AreEqual(onCompletion ? 1 : 2, candidates.Yielded);
        Assert.AreEqual(minus ? 1 : 0, Execute(minus, [Row("different")], default).Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Sparql_precancelled_join_does_not_enumerate_candidates(bool minus)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var candidates = new CancellingCandidates(cancellation, false);
        Assert.ThrowsExactly<OperationCanceledException>(() => Execute(minus, candidates, cancellation.Token));
        Assert.AreEqual(0, candidates.Yielded);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Sparql_join_preserves_duplicate_and_disjoint_mappings(bool minus)
    {
        IReadOnlyList<Dictionary<string, RdfTerm>> rows = [Row("present"), Row("present")];
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> same = [Row("present"), Row("present")];
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> disjoint = [new Dictionary<string, RdfTerm> { ["other"] = new RdfIri("urn:other") }];
        Assert.AreEqual(minus ? 0 : 4, Apply(rows, same).Count);
        Assert.AreEqual(2, Apply(rows, disjoint).Count);
        Assert.AreEqual(minus ? 2 : 0, Apply(rows, []).Count);
        if (minus)
        {
            Assert.AreEqual(2, RdfSparql.MinusSolutions(rows, same, Row("present"), default).Count);
        }
        List<Dictionary<string, RdfTerm>> Apply(IReadOnlyList<Dictionary<string, RdfTerm>> left,
            IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> right) => minus
            ? RdfSparql.MinusSolutions(left, right, null, default)
            : RdfSparql.JoinSolutions(left, right, default);
    }

    private static List<Dictionary<string, RdfTerm>> Execute(bool minus,
        IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>> candidates, CancellationToken cancellation) => minus
        ? RdfSparql.MinusSolutions([Row("present")], candidates, null, cancellation)
        : RdfSparql.JoinSolutions([Row("present")], candidates, cancellation);

    private static Dictionary<string, RdfTerm> Row(string value) => new(StringComparer.Ordinal)
    {
        ["value"] = new RdfIri("urn:" + value)
    };

    private sealed class CancellingCandidates(CancellationTokenSource cancellation, bool onCompletion)
        : IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>>
    {
        public bool Disposed
        {
            get; private set;
        }

        public int Yielded
        {
            get; private set;
        }

        public int Count => onCompletion ? 1 : 3;

        public IReadOnlyDictionary<string, RdfTerm> this[int index] => throw new NotSupportedException();

        public IEnumerator<IReadOnlyDictionary<string, RdfTerm>> GetEnumerator()
        {
            try
            {
                Yielded++;
                yield return Row("different");
                cancellation.Cancel();
                if (onCompletion)
                {
                    yield break;
                }
                Yielded++;
                yield return Row("different");
                Yielded++;
                yield return Row("different");
            }
            finally
            {
                Disposed = true;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
