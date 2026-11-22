using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("jsonres01")]
    [DataRow("jsonres02")]
    [DataRow("jsonres03")]
    [DataRow("jsonres04")]
    public async Task Sparql11_external_W3C_JSON_results_match_HTTP_JSON_and_XML(string name)
    {
        string? root = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(root))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to the pinned W3C rdf-tests checkout.");
        }
        string directory = Path.Combine(root, "sparql", "sparql11", "json-res");
        using var manifest = new VDS.RDF.Graph();
        FileLoader.Load(manifest, Path.Combine(directory, "manifest.ttl"));
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        const string qt = "http://www.w3.org/2001/sw/DataAccess/tests/test-query#";
        INode Object(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
            manifest.CreateUriNode(new Uri(predicate))).Single().Object;
        string Local(INode node)
        {
            Assert.IsInstanceOfType<IUriNode>(node);
            var uri = ((IUriNode)node).Uri;
            Assert.IsTrue(uri.IsFile);
            string path = Path.GetFullPath(uri.LocalPath);
            Assert.IsTrue(path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            return path;
        }
        var entry = manifest.CreateUriNode(new Uri("http://www.w3.org/2009/sparql/docs/tests/data-sparql11/json-res/manifest#" + name));
        var action = Object(entry, mf + "action");
        string query = await File.ReadAllTextAsync(Local(Object(action, qt + "query")));
        string data = await File.ReadAllTextAsync(Local(Object(action, qt + "data")));
        using var expected = new SparqlResultSet();
        new SparqlJsonParser().Load(expected, Local(Object(entry, mf + "result")));
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var imported = await client.PostAsync($"/v1/rdf/import?documentId={Guid.NewGuid()}&pageNumber=0",
            new StringContent(data, Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
        foreach (bool xml in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/sparql")
            {
                Content = new StringContent(query, Encoding.UTF8, "application/sparql-query")
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(xml ? "application/sparql-results+xml" : "application/sparql-results+json"));
            using var response = await client.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
            using var actual = new SparqlResultSet();
            if (xml)
            {
                new SparqlXmlParser().Load(actual, new StringReader(text));
            }
            else
            {
                new SparqlJsonParser().Load(actual, new StringReader(text));
            }
            Assert.IsTrue(expected.Equals(actual), $"{name}, XML={xml}: {text}");
            Assert.AreEqual(expected.ResultsType, actual.ResultsType);
            Assert.AreEqual(expected.Count, actual.Count);
            CollectionAssert.AreEquivalent(expected.Variables.ToArray(), actual.Variables.ToArray());
            // Both SELECT fixtures explicitly ORDER BY all projected variables.
            // Compare the sequence directly rather than relying on set equality.
            var blanks = new Dictionary<INode, INode>();
            for (int row = 0; row < expected.Count; row++)
            {
                foreach (string variable in expected.Variables)
                {
                    bool bound = expected[row].HasBoundValue(variable);
                    Assert.AreEqual(bound, actual[row].HasBoundValue(variable), $"{name}: row {row}, {variable}");
                    if (!bound)
                    {
                        continue;
                    }
                    var wanted = expected[row][variable];
                    var found = actual[row][variable];
                    if (wanted is IBlankNode)
                    {
                        Assert.IsInstanceOfType<IBlankNode>(found);
                        if (blanks.TryGetValue(wanted, out var mapped))
                        {
                            Assert.AreEqual(mapped, found);
                        }
                        else
                        {
                            Assert.IsFalse(blanks.Values.Contains(found));
                            blanks.Add(wanted, found);
                        }
                    }
                    else
                    {
                        Assert.AreEqual(wanted, found, $"{name}: row {row}, {variable}");
                    }
                }
            }
        }
    }
}
