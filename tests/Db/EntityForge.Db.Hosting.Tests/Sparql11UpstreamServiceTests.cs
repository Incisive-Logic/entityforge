using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("service1")]
    [DataRow("service2")]
    [DataRow("service3")]
    [DataRow("service4a")]
    [DataRow("service5")]
    [DataRow("service6")]
    [DataRow("service7")]
    public async Task Sparql11_external_W3C_SERVICE_evaluation(string name)
    {
        string? configured = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to a local w3c/rdf-tests checkout.");
        }
        string root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string directory = Path.Combine(root, "sparql", "sparql11", "service");
        using var manifest = new VDS.RDF.Graph();
        FileLoader.Load(manifest, Path.Combine(directory, "manifest.ttl"));
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        const string qt = "http://www.w3.org/2001/sw/DataAccess/tests/test-query#";
        IEnumerable<INode> Objects(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
            manifest.CreateUriNode(new Uri(predicate))).Select(triple => triple.Object);
        string Local(INode node)
        {
            Assert.IsInstanceOfType<IUriNode>(node);
            var uri = ((IUriNode)node).Uri;
            string path = Path.GetFullPath(uri.LocalPath);
            Assert.IsTrue(uri.IsFile && path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            return path;
        }
        var test = manifest.GetTriplesWithPredicate(manifest.CreateUriNode(new Uri(mf + "action")))
            .Single(triple => triple.Subject is IUriNode uri && uri.Uri.Fragment == "#" + name).Subject;
        var action = Objects(test, mf + "action").Single();
        var sources = Objects(action, qt + "serviceData").ToArray();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var databases = new Dictionary<string, IRdfDatabase>(StringComparer.Ordinal);
        server.MapPost("/{key}", async (string key, HttpContext context) =>
        {
            if (!databases.TryGetValue(key, out var remote))
            {
                return Results.NotFound();
            }
            using var reader = new StreamReader(context.Request.Body);
            string query = await reader.ReadToEndAsync(context.RequestAborted);
            var result = (RdfSparqlSelectResult)await remote.QuerySparqlAsync(query, context.RequestAborted);
            return Results.Json(new
            {
                head = new
                {
                    vars = RdfSparql.ResultVariables(query)
                },
                results = new
                {
                    bindings = result.Bindings.Select(row => row.ToDictionary(pair => pair.Key, pair => Wire(pair.Value)))
                }
            }, contentType: "application/sparql-results+json");
        });
        await server.StartAsync();
        var endpoints = sources.Select((_, index) => new Uri(server.Urls.Single() + "/endpoint" + index)).ToArray();
        var resources = new RdfSparqlHttpResources(endpoints, allowPrivateNetwork: true);
        string Rewrite(string query)
        {
            for (int index = 0; index < sources.Length; index++)
            {
                string endpoint = ((IUriNode)Objects(sources[index], qt + "endpoint").Single()).Uri.AbsoluteUri;
                query = query.Replace("<" + endpoint + ">", "<" + endpoints[index] + ">", StringComparison.Ordinal);
            }
            return query.Replace("<http://invalid.endpoint.org/sparql>", "<" + server.Urls.Single() + "/missing>", StringComparison.Ordinal);
        }
        async Task Load(IRdfDatabase database, IEnumerable<INode> files)
        {
            var mutations = new List<RdfMutation>();
            foreach (var file in files)
            {
                using var graph = new VDS.RDF.Graph();
                string path = Local(file);
                FileLoader.Load(graph, path);
                mutations.AddRange(graph.Triples.Select(triple => new AssertRdfQuad(new(Relocate(RdfSyntax.FromNode(triple.Subject, path)),
                    (RdfIri)RdfSyntax.FromNode(triple.Predicate, path), Relocate(RdfSyntax.FromNode(triple.Object, path))))));
            }
            await database.CommitAsync(new(Guid.NewGuid(), mutations));
        }
        try
        {
            for (int index = 0; index < sources.Length; index++)
            {
                var database = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
                {
                    HttpResources = resources
                });
                databases.Add("endpoint" + index, database);
                await Load(database, Objects(sources[index], qt + "data"));
            }
            await using var local = await RdfDatabase.OpenLocalAsync(new(CreateDirectory())
            {
                HttpResources = resources
            });
            await Load(local, Objects(action, qt + "data"));
            string query = Rewrite(File.ReadAllText(Local(Objects(action, qt + "query").Single())));
            var actual = (RdfSparqlSelectResult)await local.QuerySparqlAsync(query);
            using var expected = new SparqlResultSet();
            new SparqlXmlParser().Load(expected, Local(Objects(test, mf + "result").Single()));
            string[] variables = expected.Variables.Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEquivalent(variables, RdfSparql.ResultVariables(query).ToArray());
            foreach (var row in actual.Bindings)
            {
                Assert.IsTrue(row.Keys.All(variable => variables.Contains(variable, StringComparer.Ordinal)),
                    "SERVICE composition returned a binding outside the projected result variables.");
            }
            string Key(IEnumerable<RdfTerm?> terms) => JsonSerializer.Serialize(terms);
            var expectedRows = expected.Select(row => Key(variables.Select(variable => row.HasValue(variable) && row[variable] is { } value
                ? Relocate(RdfSyntax.FromNode(value, "expected")) : null))).Order(StringComparer.Ordinal).ToArray();
            var actualRows = actual.Bindings.Select(row => Key(variables.Select(variable => row.GetValueOrDefault(variable))))
                .Order(StringComparer.Ordinal).ToArray();
            CollectionAssert.AreEqual(expectedRows, actualRows, name);
            if (Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_HTTP_REPORT") is { Length: > 0 } report)
            {
                File.WriteAllText(report + ".service-" + name + ".json",
                    JsonSerializer.Serialize(new
                    {
                        cases = new[] { new { name = test.ToString(), passed = true } }
                    }));
            }
        }
        finally
        {
            await server.StopAsync();
            foreach (var database in databases.Values)
            {
                await database.DisposeAsync();
            }
        }

        RdfTerm Relocate(RdfTerm term) => term is RdfIri iri
            ? new RdfIri(Rewrite("<" + iri.Value + ">")[1..^1]) : term;

        static object Wire(RdfTerm term) => term switch
        {
            RdfIri iri => new { type = "uri", value = iri.Value },
            RdfBlankNode blank => new { type = "bnode", value = blank.Identifier },
            RdfLiteral literal when literal.Language is not null => new Dictionary<string, string>
            {
                ["type"] = "literal",
                ["value"] = literal.LexicalForm,
                ["xml:lang"] = literal.Language
            },
            RdfLiteral literal => new { type = "literal", value = literal.LexicalForm, datatype = literal.Datatype },
            _ => throw new InvalidDataException("Unexpected fixture term.")
        };
    }
}
