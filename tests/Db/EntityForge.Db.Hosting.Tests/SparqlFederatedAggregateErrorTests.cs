using System.Globalization;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SUM", false)]
    [DataRow("SUM", true)]
    [DataRow("AVG", false)]
    [DataRow("AVG", true)]
    [DataRow("GROUP_CONCAT", false)]
    [DataRow("GROUP_CONCAT", true)]
    [DataRow("COUNT", false)]
    [DataRow("COUNT", true)]
    public async Task Sparql_federated_aggregate_argument_errors_preserve_groups_and_update_replay(
        string function, bool distinct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        int fetched = 0;
        server.MapPost("/sparql", () =>
        {
            Interlocked.Increment(ref fetched);
            return Results.Text("""
                {"head":{"vars":["remote"]},"results":{"bindings":[{"remote":{"type":"uri","value":"urn:ok"}}]}}
                """, "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoint = new Uri(server.Urls.Single() + "/sparql");
        string value = $"IF(EXISTS {{ SERVICE <{endpoint}> {{ <urn:s> <urn:p> ?remote }} }},1/?v,0)";
        if (function == "GROUP_CONCAT")
        {
            value = "STR(" + value + ")";
        }
        string select = "SELECT ?g (" + function + "(" + (distinct ? "DISTINCT " : "") + value +
            ") AS ?result) WHERE { VALUES (?g ?v) { (<urn:a> 1) (<urn:a> 1) (<urn:b> 0) (<urn:b> 1) (<urn:c> 1) } } GROUP BY ?g";
        string update = "INSERT { ?g <urn:group> ?g ; <urn:result> ?result . [] <urn:marker> ?g } WHERE { { " + select + " } }";
        string directory = CreateDirectory();
        Guid transaction = Guid.NewGuid();
        RdfSparqlUpdateCommit? receipt = null;
        for (int restart = 0; restart < 2; restart++)
        {
            await using var database = await RdfDatabase.OpenLocalAsync(new(directory)
            {
                HttpResources = new([endpoint], allowPrivateNetwork: true)
            });
            if (restart == 0)
            {
                var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(select);
                Assert.AreEqual(3, result.Bindings.Count);
                foreach (var row in result.Bindings)
                {
                    string group = ((RdfIri)row["g"]).Value;
                    bool valid = group != "urn:b" || function == "COUNT";
                    CollectionAssert.AreEquivalent(valid ? new[] { "g", "result" } : new[] { "g" }, row.Keys.ToArray());
                    if (valid)
                    {
                        Verify(group, row["result"]);
                    }
                }
                Assert.AreEqual(1, fetched, "Identical remote patterns share one clause result within this execution.");
            }
            var committed = await database.CommitSparqlUpdateAsync(transaction, update);
            receipt ??= committed;
            Assert.AreEqual(receipt, committed);
            Assert.AreEqual(2, fetched, "Restart replay must neither fetch again nor allocate new blank nodes.");
            var quads = database.Find(new()).ToArray();
            Assert.AreEqual(function == "COUNT" ? 9 : 8, quads.Length);
            var markers = quads.Where(quad => quad.Predicate == new RdfIri("urn:marker")).ToArray();
            Assert.AreEqual(3, markers.Length);
            Assert.AreEqual(3, markers.Select(quad => quad.Subject).Distinct().Count());
            Assert.IsTrue(markers.All(quad => quad.Subject is RdfBlankNode));
            var values = quads.Where(quad => quad.Predicate == new RdfIri("urn:result")).ToArray();
            CollectionAssert.AreEquivalent(function == "COUNT" ? new[] { "urn:a", "urn:b", "urn:c" } : new[] { "urn:a", "urn:c" },
                values.Select(quad => ((RdfIri)quad.Subject).Value).ToArray());
            foreach (var quad in values)
            {
                Verify(((RdfIri)quad.Subject).Value, quad.Object);
            }
        }
        await server.StopAsync();

        void Verify(string group, RdfTerm term)
        {
            var literal = (RdfLiteral)term;
            Assert.AreEqual("http://www.w3.org/2001/XMLSchema#" +
                (function == "GROUP_CONCAT" ? "string" : function == "COUNT" ? "integer" : "decimal"), literal.Datatype);
            bool doubled = group == "urn:a" && !distinct;
            if (function == "GROUP_CONCAT")
            {
                var parts = literal.LexicalForm.Split(' ');
                Assert.AreEqual(doubled ? 2 : 1, parts.Length);
                Assert.IsTrue(parts.All(part => Decimal.Parse(part, CultureInfo.InvariantCulture) == 1m));
            }
            else
            {
                Assert.AreEqual(doubled && function != "AVG" ? 2m : 1m,
                    Decimal.Parse(literal.LexicalForm, CultureInfo.InvariantCulture));
            }
        }
    }
}
