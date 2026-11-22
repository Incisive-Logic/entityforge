using System.Text.Json;
using EntityForge.Db.Rdf;
using VDS.RDF;
using VDS.RDF.Parsing;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, "sparql11/syntax-query")]
    [DataRow(true, "sparql11/syntax-query")]
    [DataRow(true, "sparql11/syntax-fed")]
    [DataRow(true, "sparql10/syntax-sparql1")]
    [DataRow(true, "sparql10/syntax-sparql2")]
    [DataRow(true, "sparql10/syntax-sparql3")]
    [DataRow(false, "sparql10/syntax-sparql3")]
    [DataRow(true, "sparql10/syntax-sparql4")]
    [DataRow(false, "sparql10/syntax-sparql4")]
    [DataRow(true, "sparql10/syntax-sparql5")]
    [DataRow(false, "sparql11/aggregates")]
    [DataRow(false, "sparql11/construct")]
    [DataRow(false, "sparql11/grouping")]
    public void Sparql11_external_W3C_query_syntax_matches_manifest(bool positive, string category)
    {
        string? configured = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to an upstream w3c/rdf-tests checkout.");
        }
        string root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var manifest = new VDS.RDF.Graph();
        FileLoader.Load(manifest, Path.Combine(root, "sparql", category, "manifest.ttl"));
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        IEnumerable<INode> Objects(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
            manifest.CreateUriNode(new Uri(predicate))).Select(triple => triple.Object);
        var entries = new List<INode>();
        foreach (var list in manifest.GetTriplesWithPredicate(manifest.CreateUriNode(new Uri(mf + "entries"))))
        {
            INode current = list.Object;
            var visited = new HashSet<INode>();
            while (current is not IUriNode end || end.Uri.AbsoluteUri != RdfSpecsHelper.RdfListNil)
            {
                Assert.IsTrue(visited.Add(current));
                entries.Add(Objects(current, RdfSpecsHelper.RdfListFirst).Single());
                current = Objects(current, RdfSpecsHelper.RdfListRest).Single();
            }
        }
        var results = new List<object>();
        var unexpected = new List<string>();
        foreach (var test in entries.Where(entry => Objects(entry, RdfSpecsHelper.RdfType)
            .Any(type => type is IUriNode uri && uri.Uri.AbsoluteUri == mf + (positive ? "PositiveSyntaxTest" : "NegativeSyntaxTest") +
                (category.StartsWith("sparql11", StringComparison.Ordinal) ? "11" : ""))))
        {
            var file = (IUriNode)Objects(test, mf + "action").Single();
            string path = Path.GetFullPath(file.Uri.LocalPath);
            Assert.IsTrue(file.Uri.IsFile && path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            bool rejected = false;
            string? error = null;
            try
            {
                string query = "BASE <" + file.Uri.AbsoluteUri + ">\n" + File.ReadAllText(path);
                if (category == "sparql11/syntax-fed")
                {
                    // Parse the native SELECT plan without contacting the fixture's
                    // external SERVICE addresses. Evaluation has separate fixtures.
                    _ = RdfSparql.ResultVariables(query);
                }
                else
                {
                    RdfSparql.ExecuteRelation([], query, default);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or NotSupportedException)
            {
                rejected = true;
                error = exception.Message;
            }
            if (rejected == positive)
            {
                unexpected.Add(path + ": " + error);
            }
            results.Add(new
            {
                path,
                rejected,
                error
            });
        }
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_SYNTAX_REPORT") is { Length: > 0 } report)
        {
            File.WriteAllText(report + "." + category.Replace('/', '-') + (positive ? ".positive.json" : ".negative.json"),
                JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual(0, unexpected.Count, "Unexpected upstream syntax results: " + String.Join("; ", unexpected));
    }
}
