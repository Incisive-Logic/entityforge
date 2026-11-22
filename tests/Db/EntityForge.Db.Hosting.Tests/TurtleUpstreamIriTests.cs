using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class TurtleUpstreamIriTests
{
    [TestMethod]
    public void Turtle_external_W3C_Rdf11_manifest_preserves_native_terms()
    {
        string? root = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to the pinned W3C RDF test checkout.");
        }
        string directory = Path.Combine(root!, "rdf", "rdf11", "rdf-turtle");
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        const string rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        using var manifest = new VDS.RDF.Graph();
        FileLoader.Load(manifest, Path.Combine(directory, "manifest.ttl"));
        IEnumerable<INode> Objects(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
            manifest.CreateUriNode(new Uri(predicate))).Select(triple => triple.Object);
        string testBase = ((IUriNode)manifest.GetTriplesWithPredicate(manifest.CreateUriNode(new Uri(mf + "assumedTestBase"))).Single().Object).Uri.OriginalString;
        var failures = new List<string>();
        int passed = 0;
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
                Assert.IsTrue(kind is "#TestTurtlePositiveSyntax" or "#TestTurtleNegativeSyntax" or "#TestTurtleEval" or "#TestTurtleNegativeEval", kind);
                string action = ((IUriNode)Objects(entry, mf + "action").Single()).Uri.LocalPath;
                bool negative = kind is "#TestTurtleNegativeSyntax" or "#TestTurtleNegativeEval";
                try
                {
                    string source = "@base <" + testBase + Path.GetRelativePath(directory, action).Replace('\\', '/') + "> .\n" + File.ReadAllText(action);
                    IReadOnlyList<RdfQuad>? actual = null;
                    Exception? error = null;
                    try
                    {
                        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
                        actual = RdfSyntax.ParseTurtle(input, "actual");
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
                        if (kind == "#TestTurtleEval")
                        {
                            string expectedPath = ((IUriNode)Objects(entry, mf + "result").Single()).Uri.LocalPath;
                            // The expected file already contains absolute N-Triples terms.
                            // Use the separate DATA term reader to keep expected-term
                            // construction independent of the RDF text token binding.
                            using var expected = IdentityGraph(RdfSparqlUpdate.Parse("INSERT DATA {\n" + File.ReadAllText(expectedPath) + "\n}", "expected")
                                .Cast<AssertRdfQuad>().Select(mutation => mutation.Quad));
                            using var result = IdentityGraph(actual!);
                            Assert.IsTrue(expected.Equals(result), "Native RDF terms differ from the expected graph.");
                        }
                    }
                    passed++;
                }
                catch (Exception failure)
                {
                    failures.Add(Path.GetFileName(action) + ": " + failure.Message);
                }
            }
        }
        Console.WriteLine($"W3C Turtle: {passed} passed, {failures.Count} failed.");
        Assert.IsTrue(passed > 0);
        Assert.AreEqual(0, failures.Count, String.Join("\n", failures));
    }

    private static VDS.RDF.Graph IdentityGraph(IEnumerable<RdfQuad> quads)
    {
        // Encode all nonblank terms before using the dependency's graph-isomorphism
        // matcher, so its URL equivalence cannot conceal native IRI differences.
        var graph = new VDS.RDF.Graph();
        INode Node(RdfTerm term) => term is RdfBlankNode blank
            ? graph.CreateBlankNode(Convert.ToHexString(Encoding.UTF8.GetBytes(blank.Identifier)))
            : graph.CreateUriNode(new Uri("urn:term:" + Convert.ToHexString(Encoding.UTF8.GetBytes(RdfSyntax.FormatTerm(term)))));
        foreach (var quad in quads)
        {
            Assert.IsNull(quad.Graph);
            graph.Assert(Node(quad.Subject), Node(quad.Predicate), Node(quad.Object));
        }
        return graph;
    }
}
