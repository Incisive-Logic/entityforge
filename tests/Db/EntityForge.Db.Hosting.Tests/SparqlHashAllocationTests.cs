using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("MD5")]
    [DataRow("SHA1")]
    [DataRow("SHA256")]
    [DataRow("SHA384")]
    [DataRow("SHA512")]
    public void Sparql_hashes_preserve_UTF8_across_chunks_without_input_sized_scratch(string name)
    {
        var algorithm = new HashAlgorithmName(name);
        byte[] Expected(string value)
        {
            using var reference = IncrementalHash.CreateHash(algorithm);
            reference.AppendData(Encoding.UTF8.GetBytes(value));
            return reference.GetHashAndReset();
        }
        // Place supplementary characters on both sides of the byte-buffer
        // boundary, and cover the existing shared helper's fallback contract.
        foreach (string text in new[] { "", "abc", new string('a', 1023) + "😀é", new string('é', 511) + "😀", "a\uD800b\uDC00" })
        {
            CollectionAssert.AreEqual(Expected(text), DatabaseUtf8Hash.Hash(text, algorithm));
        }
        string large = String.Concat(Enumerable.Repeat(new string('a', 1023) + "😀é", 1024));
        byte[] expected = Expected(large);
        _ = DatabaseUtf8Hash.Hash(large, algorithm);
        long start = GC.GetAllocatedBytesForCurrentThread();
        byte[] actual = DatabaseUtf8Hash.Hash(large, algorithm);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        CollectionAssert.AreEqual(expected, actual);
        Assert.IsTrue(allocated < 64 * 1024, $"Hash scratch allocated {allocated} bytes for {large.Length} UTF16 units.");
        RdfQuad[] data = [new(new RdfIri("urn:s"), new("urn:p"), new RdfLiteral(large))];
        var row = ((RdfSparqlSelectResult)RdfSparql.ExecuteRelation(data,
            "SELECT (" + name + "(?v) AS ?digest) { <urn:s> <urn:p> ?v }", default)).Bindings.Single();
        Assert.AreEqual(new RdfLiteral(Convert.ToHexStringLower(expected)), row["digest"]);
    }
}
