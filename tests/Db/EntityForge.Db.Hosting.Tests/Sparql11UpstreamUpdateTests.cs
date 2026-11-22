using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("syntax-update-1", false, 13)]
    [DataRow("syntax-update-1", true, 41)]
    [DataRow("syntax-update-2", true, 1)]
    [DataRow("delete-insert", false, 8)]
    public void Sparql11_external_W3C_update_syntax_acceptance_matches_manifest(string category, bool positive, int expected)
    {
        string? configured = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to an upstream w3c/rdf-tests checkout.");
        }
        string root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var manifest = new VDS.RDF.Graph();
        FileLoader.Load(manifest, Path.Combine(root, "sparql", "sparql11", category, "manifest.ttl"));
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        var failures = new List<string>();
        var results = new List<object>();
        int executed = 0;
        IEnumerable<INode> Objects(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
            manifest.CreateUriNode(new Uri(predicate))).Select(triple => triple.Object);
        var entries = new List<INode>();
        foreach (var list in manifest.GetTriplesWithPredicate(manifest.CreateUriNode(new Uri(mf + "entries"))))
        {
            INode current = list.Object;
            var visited = new HashSet<INode>();
            while (current is not IUriNode end || end.Uri.AbsoluteUri != RdfSpecsHelper.RdfListNil)
            {
                Assert.IsTrue(visited.Add(current), "Cyclic upstream manifest list.");
                entries.Add(Objects(current, RdfSpecsHelper.RdfListFirst).Single());
                current = Objects(current, RdfSpecsHelper.RdfListRest).Single();
            }
        }
        string type = mf + (positive ? "Positive" : "Negative") + (category == "delete-insert" ? "SyntaxTest11" : "UpdateSyntaxTest11");
        foreach (var test in entries.Where(entry => Objects(entry, RdfSpecsHelper.RdfType)
            .Any(node => node is IUriNode uri && uri.Uri.AbsoluteUri == type)))
        {
            var file = (IUriNode)manifest.GetTriplesWithSubjectPredicate(test, manifest.CreateUriNode(new Uri(mf + "action"))).Single().Object;
            string path = Path.GetFullPath(file.Uri.LocalPath);
            if (!file.Uri.IsFile || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("An upstream fixture escapes its checkout.");
            }
            executed++;
            int previousFailures = failures.Count;
            string update = "BASE <" + file.Uri.AbsoluteUri + ">\n" + File.ReadAllText(path);
            try
            {
                try
                {
                    _ = RdfSparqlUpdate.Parse(update, "negative");
                }
                catch (NotSupportedException)
                {
                    // Syntax fixtures parse LOAD commands without retrieving external documents.
                    // Runtime loading and failures have separate HTTP integration coverage.
                    var loads = RdfSparqlUpdate.ParseLoads(update, "negative").ToDictionary(item => item.Scope,
                        _ => (IReadOnlyList<RdfMutation>)Array.Empty<RdfMutation>(), StringComparer.Ordinal);
                    _ = RdfSparqlUpdate.ParseWhere(Array.Empty<RdfQuad>(), update, "negative", default, loads);
                }
                if (!positive)
                {
                    failures.Add(test + ": accepted negative syntax");
                }
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException)
            {
                if (positive)
                {
                    failures.Add(test + ": " + error);
                }
            }
            results.Add(new
            {
                name = test.ToString(),
                requestFile = path,
                passed = failures.Count == previousFailures
            });
        }
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_UPDATE_SYNTAX_REPORT") is { Length: > 0 } report)
        {
            File.WriteAllText(report + "." + category + (positive ? ".positive.json" : ".negative.json"),
                JsonSerializer.Serialize(new
                {
                    cases = results
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.AreEqual(expected, executed);
        Assert.AreEqual(0, failures.Count, String.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public async Task Sparql11_external_W3C_update_evaluation_manifest()
    {
        string? configured = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to an upstream w3c/rdf-tests checkout.");
        }
        string root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        const string ut = "http://www.w3.org/2009/sparql/tests/test-update#";
        var results = new List<object>();
        var failures = new List<string>();
        int passed = 0;
        string Local(INode node)
        {
            if (node is not IUriNode uri || !uri.Uri.IsFile)
            {
                throw new InvalidDataException("An upstream update fixture must be a local file.");
            }
            string path = Path.GetFullPath(uri.Uri.LocalPath);
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("An upstream fixture escapes its checkout.");
            }
            return path;
        }
        foreach (string category in new[] { "basic-update", "delete", "delete-data", "delete-insert", "delete-where", "add", "copy", "move", "clear", "drop", "update-silent" })
        {
            using var manifest = new VDS.RDF.Graph();
            FileLoader.Load(manifest, Path.Combine(root, "sparql", "sparql11", category, "manifest.ttl"));
            IEnumerable<INode> Objects(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
                manifest.CreateUriNode(new Uri(predicate))).Select(t => t.Object);
            var entries = new List<INode>();
            foreach (var list in manifest.GetTriplesWithPredicate(manifest.CreateUriNode(new Uri(mf + "entries"))))
            {
                INode current = list.Object;
                var visited = new HashSet<INode>();
                while (current is not IUriNode end || end.Uri.AbsoluteUri != RdfSpecsHelper.RdfListNil)
                {
                    Assert.IsTrue(visited.Add(current), "Cyclic upstream manifest list.");
                    entries.Add(Objects(current, RdfSpecsHelper.RdfListFirst).Single());
                    current = Objects(current, RdfSpecsHelper.RdfListRest).Single();
                }
            }
            var evaluationType = manifest.CreateUriNode(new Uri(mf + "UpdateEvaluationTest"));
            var selected = entries.Where(entry => Objects(entry, RdfSpecsHelper.RdfType).Contains(evaluationType)).ToArray();
            var declared = manifest.GetTriplesWithPredicateObject(manifest.CreateUriNode(new Uri(RdfSpecsHelper.RdfType)),
                evaluationType).Select(triple => triple.Subject).ToArray();
            Assert.IsTrue(selected.Length > 0, "The selected update category contains no evaluation tests.");
            Assert.AreEqual(selected.Length, selected.Distinct().Count(), "Duplicate update manifest entry.");
            CollectionAssert.AreEquivalent(declared, selected,
                "Unlisted update definitions require an explicit corpus disposition.");
            foreach (var test in selected)
            {
                string name = category + "/" + test;
                string? requestFile = null;
                try
                {
                    List<RdfQuad> Dataset(INode description)
                    {
                        var quads = new List<RdfQuad>();
                        void Load(INode file, RdfTerm? graphName)
                        {
                            string path = Local(file);
                            using var data = new VDS.RDF.Graph();
                            FileLoader.Load(data, path);
                            quads.AddRange(data.Triples.Select(t => new RdfQuad(RdfSyntax.FromNode(t.Subject, path),
                                (RdfIri)RdfSyntax.FromNode(t.Predicate, path), RdfSyntax.FromNode(t.Object, path), graphName)));
                        }
                        foreach (var file in Objects(description, ut + "data"))
                        {
                            Load(file, null);
                        }
                        foreach (var named in Objects(description, ut + "graphData"))
                        {
                            var label = Objects(named, "http://www.w3.org/2000/01/rdf-schema#label").Single();
                            string iri = label is IUriNode resource ? resource.Uri.OriginalString : ((ILiteralNode)label).Value;
                            Load(Objects(named, ut + "graph").Single(), new RdfIri(iri));
                        }
                        return quads;
                    }
                    var action = Objects(test, mf + "action").Single();
                    var expected = Dataset(Objects(test, mf + "result").Single());
                    var actual = Dataset(action).ToHashSet();
                    requestFile = Local(Objects(action, ut + "request").Single());
                    string update = "BASE <" + new Uri(requestFile).AbsoluteUri + ">\n" + File.ReadAllText(requestFile);
                    IReadOnlyList<RdfMutation> mutations;
                    try
                    {
                        mutations = RdfSparqlUpdate.Parse(update, name);
                    }
                    catch (NotSupportedException)
                    {
                        var loads = await RdfSparqlUpdate.PrepareLoadsAsync(update, name, null, default);
                        mutations = RdfSparqlUpdate.ParseWhere(actual.ToArray(), update, name, default, loads);
                    }
                    foreach (var mutation in mutations)
                    {
                        if (mutation is AssertRdfQuad added)
                        {
                            actual.Add(added.Quad);
                        }
                        else if (mutation is RetractRdfQuad removed)
                        {
                            actual.Remove(removed.Quad);
                        }
                    }
                    using var wanted = QuadGraph(expected);
                    using var observed = QuadGraph(actual);
                    if (!wanted.Equals(observed))
                    {
                        throw new InvalidDataException($"Dataset differs: expected {expected.Count} quads, actual {actual.Count}.");
                    }
                    passed++;
                    results.Add(new
                    {
                        name,
                        requestFile,
                        passed = true
                    });
                }
                catch (Exception error)
                {
                    failures.Add(name + ": " + error.Message);
                    results.Add(new
                    {
                        name,
                        requestFile,
                        passed = false,
                        error = error.ToString()
                    });
                }
            }
        }
        string? report = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_UPDATE_REPORT");
        if (!String.IsNullOrWhiteSpace(report))
        {
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                passed,
                failed = failures.Count,
                cases = results
            },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.AreEqual(0, failures.Count, String.Join(Environment.NewLine, failures));
    }

    private static VDS.RDF.Graph QuadGraph(IEnumerable<RdfQuad> quads)
    {
        var graph = new VDS.RDF.Graph();
        int index = 0;
        foreach (var quad in quads.Distinct())
        {
            var row = graph.CreateBlankNode("quad-row-" + index++);
            void Add(string field, RdfTerm value) => graph.Assert(row,
                graph.CreateUriNode(new Uri("urn:quad:" + field)), RdfSyntax.ToNode(graph, value));
            Add("subject", quad.Subject);
            Add("predicate", quad.Predicate);
            Add("object", quad.Object);
            if (quad.Graph is not null)
            {
                Add("graph", quad.Graph);
            }
        }
        return graph;
    }
}
