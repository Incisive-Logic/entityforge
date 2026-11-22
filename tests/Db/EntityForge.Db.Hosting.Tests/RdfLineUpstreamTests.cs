using EntityForge.Db.Rdf;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfLineUpstreamTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Rdf_line_external_W3C_Rdf11_syntax_manifest(bool dataset)
    {
        string? root = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to the pinned W3C RDF test checkout.");
        }
        string directory = Path.Combine(root!, "rdf", "rdf11", dataset ? "rdf-n-quads" : "rdf-n-triples");
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        const string rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        using var manifest = new VDS.RDF.Graph();
        FileLoader.Load(manifest, Path.Combine(directory, "manifest.ttl"));
        IEnumerable<INode> Objects(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
            manifest.CreateUriNode(new Uri(predicate))).Select(triple => triple.Object);
        int passed = 0;
        var failures = new List<string>();
        string typePrefix = dataset ? "#TestNQuads" : "#TestNTriples";
        foreach (var entryList in manifest.GetTriplesWithPredicate(manifest.CreateUriNode(new Uri(mf + "entries"))))
        {
            INode current = entryList.Object;
            var visited = new HashSet<INode>();
            while (current is not IUriNode end || end.Uri.OriginalString != rdf + "nil")
            {
                Assert.IsTrue(visited.Add(current), "Cyclic upstream manifest list.");
                INode entry = Objects(current, rdf + "first").Single();
                current = Objects(current, rdf + "rest").Single();
                string kind = ((IUriNode)Objects(entry, rdf + "type").Single()).Uri.Fragment;
                Assert.IsTrue(kind == typePrefix + "PositiveSyntax" || kind == typePrefix + "NegativeSyntax", kind);
                string action = ((IUriNode)Objects(entry, mf + "action").Single()).Uri.LocalPath;
                bool negative = kind == typePrefix + "NegativeSyntax";
                try
                {
                    Exception? error = null;
                    try
                    {
                        using var input = File.OpenRead(action);
                        _ = dataset ? RdfSyntax.ParseNQuads(input, "scope") : RdfSyntax.ParseNTriples(input, "scope");
                    }
                    catch (Exception failure) when (failure is InvalidDataException or ArgumentException)
                    {
                        error = failure;
                    }
                    if (negative)
                    {
                        Assert.IsNotNull(error, "Negative fixture was accepted.");
                    }
                    else
                    {
                        Assert.IsNull(error, error?.ToString());
                    }
                    passed++;
                }
                catch (Exception failure)
                {
                    failures.Add(Path.GetFileName(action) + ": " + failure.Message);
                }
            }
        }
        Console.WriteLine($"W3C {(dataset ? "N-Quads" : "N-Triples")}: {passed} passed, {failures.Count} failed.");
        Assert.IsTrue(passed > 0);
        Assert.AreEqual(0, failures.Count, String.Join("\n", failures));
    }
}
