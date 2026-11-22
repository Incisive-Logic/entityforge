using System.Collections;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SparqlTabularCancellationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Cancelled_tabular_result_does_not_return_even_an_empty_document(bool tsv)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = new RdfSparqlSelectResult(new(0), []);
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            SparqlProtocolEndpoint.Tabular(result, [], tsv, cancellation.Token));
        Assert.AreEqual(tsv ? "?x\n" : "x\r\n",
            SparqlProtocolEndpoint.Tabular(result, ["x"], tsv, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Tabular_serialization_stops_when_cancellation_arrives_between_rows(bool tsv)
    {
        using var cancellation = new CancellationTokenSource();
        var rows = new CancellingRows(cancellation);
        Assert.ThrowsExactly<OperationCanceledException>(() => SparqlProtocolEndpoint.Tabular(
            new(new(0), rows), ["x"], tsv, cancellation.Token));
        Assert.AreEqual(2, rows.Visited);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Excessive_tabular_value_is_rejected_before_allocating_an_escaped_copy(bool tsv)
    {
        // Allocate the input before observing serializer allocations. Quoting
        // doubles this payload if the output limit is checked only afterwards.
        var value = new RdfLiteral(new string('"', 16_777_217));
        var result = new RdfSparqlSelectResult(new(0), [new Dictionary<string, RdfTerm> { ["x"] = value }]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() =>
            SparqlProtocolEndpoint.Tabular(result, ["x"], tsv, CancellationToken.None));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < 4 * 1024 * 1024, $"An excessive term allocated {allocated} bytes before rejection.");
    }

    private sealed class CancellingRows(CancellationTokenSource cancellation) : IReadOnlyList<IReadOnlyDictionary<string, RdfTerm>>
    {
        public int Count => 3;

        public int Visited
        {
            get; private set;
        }

        public IReadOnlyDictionary<string, RdfTerm> this[int index] => throw new NotSupportedException();

        public IEnumerator<IReadOnlyDictionary<string, RdfTerm>> GetEnumerator()
        {
            var row = new Dictionary<string, RdfTerm> { ["x"] = new RdfLiteral("value") };
            Visited++;
            yield return row;
            cancellation.Cancel();
            Visited++;
            yield return row;
            Assert.Fail("Serialization continued after cancellation.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
