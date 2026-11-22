using System.Security.Cryptography;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfPhysicalKeyCompatibilityTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Physical_index_keys_preserve_existing_format_for_all_term_kinds(bool remove)
    {
        var predicate = new RdfIri("urn:predicate:λ");
        RdfTerm[] objects =
        [
            new RdfIri("urn:object:λ"), new RdfBlankNode("object"),
            new RdfLiteral(""), new RdfLiteral("λ😀", language: "EN"),
            new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfTripleTerm(new RdfBlankNode("quoted"), predicate, new RdfLiteral("nested"))
        ];
        foreach (var value in objects)
        {
            foreach (RdfTerm? graph in new RdfTerm?[] { null, new RdfIri("urn:graph"), new RdfBlankNode("graph") })
            {
                var quad = new RdfQuad(new RdfBlankNode("subject"), predicate, value, graph);
                RdfMutation mutation = remove ? new RetractRdfQuad(quad) : new AssertRdfQuad(quad);
                var entries = RdfPhysicalAccessPath.Compile(new(Guid.NewGuid(), [mutation]));
                byte[] encoded = RdfPhysicalCodec.EncodeQuad(quad);
                byte[] reference = SHA256.HashData(encoded)[..16];
                string[] expected =
                [
                    "q:" + Convert.ToBase64String(reference), LegacyKey("s", quad.Subject),
                    LegacyKey("p", predicate), LegacyKey("o", value),
                    graph is null ? "d:" : LegacyKey("g", graph)
                ];
                CollectionAssert.AreEqual(expected, entries.Select(entry => entry.Key).ToArray());
                Assert.IsTrue(entries.All(entry => entry.Kind ==
                    (remove ? AppendMergeMutationKind.Remove : AppendMergeMutationKind.Add)));
                CollectionAssert.AreEqual(encoded, entries[0].Value);
                foreach (var entry in entries.Skip(1))
                {
                    CollectionAssert.AreEqual(reference, entry.Value);
                }
            }
        }
    }

    // The pre-optimization encoding is a compatibility oracle: changing it
    // would make existing physical index entries unreachable after reopening.
    private static string LegacyKey(string family, RdfTerm term) =>
        family + ":" + Convert.ToBase64String(SHA256.HashData(RdfPhysicalCodec.EncodeTerm(term))[..16]);
}
