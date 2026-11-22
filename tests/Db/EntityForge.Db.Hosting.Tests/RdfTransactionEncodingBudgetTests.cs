using System.Collections;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfTransactionEncodingBudgetTests
{
    [TestMethod]
    [DataRow("command")]
    [DataRow("interpretation")]
    [DataRow("mutation")]
    [DataRow("read")]
    public void Encoded_byte_bound_covers_every_payload_section_and_accepts_exact_boundary(string section)
    {
        string text = new('x', 16_384);
        var subject = new RdfIri("urn:subject");
        var predicate = new RdfIri("urn:predicate");
        var transaction = new RdfTransaction(Guid.NewGuid(),
            section == "mutation" ? [new AssertRdfQuad(new(subject, predicate, new RdfLiteral(text)))] : [],
            SourceCommandHash: section == "command" ? text : null,
            ReadPredicates: section == "read"
                ? [new RdfSubjectStateRead(new RdfIri("urn:" + text), null, true, "rdf-subject-state:v1:" + new string('0', 64))]
                : null,
            InterpretationFingerprint: section == "interpretation" ? text : null);
        byte[] encoded = RdfTransactionCodec.Encode(transaction);
        CollectionAssert.AreEqual(encoded, RdfTransactionCodec.Encode(transaction, encoded.Length));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfTransactionCodec.Encode(transaction, encoded.Length - 1));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfTransactionCodec.Encode(transaction, 128));
        CollectionAssert.AreEqual(encoded, RdfTransactionCodec.Encode(
            RdfTransactionCodec.DecodeTransaction(transaction.TransactionId, encoded)));
    }

    [TestMethod]
    [DataRow(false, -1)]
    [DataRow(false, 10_000_001)]
    [DataRow(true, -1)]
    [DataRow(true, 10_000_001)]
    public void Transaction_rejects_unrecoverable_collection_counts_before_enumerating(bool reads, int count)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = reads
            ? new RdfTransaction(Guid.NewGuid(), [], ReadPredicates: new UnreadableList<RdfReadPredicate>(count))
            : new RdfTransaction(Guid.NewGuid(), new UnreadableList<RdfMutation>(count)));
    }

    private sealed class UnreadableList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;

        public T this[int index] => throw new InvalidOperationException("Input must not be consumed.");

        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Input must not be consumed.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
