using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Hosting;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class PartitionQuerySelectionTests
{
    [TestMethod]
    [DataRow("MATCH (n:Person) WHERE n.name = \"one\" RETURN n")]
    [DataRow("MATCH (n:Person) RETURN COUNT(n) AS total")]
    [DataRow("MATCH (n:Person)-[e:peer]->(t:Person) RETURN n, t")]
    [DataRow("MATCH (n:Person) OPTIONAL MATCH (n)-[e:peer]->(t:Person) RETURN n, t")]
    [DataRow("MATCH (n:Person)-[e:peer]->(t:Person) RETURN COUNT(e) AS total")]
    [DataRow("MATCH (n:Person) OPTIONAL MATCH (n)-[e:peer]->(t:Person) RETURN COUNT(t) AS total")]
    [DataRow("MATCH (n:Person)-[e:peer]->{1,2}(t:Person) RETURN n, t")]
    public async Task Graph_pruning_preserves_full_relation_answers(string statement)
    {
        var vertices = Enumerable.Range(0, 4).Select(i => new GraphVertex(new(Guid.NewGuid()),
            new HashSet<string> { i < 2 ? "Person" : "Other" },
            new Dictionary<string, DatabaseValue> { ["name"] = new DatabaseValue.String(i == 0 ? "one" : "two") })).ToArray();
        var edges = new[]
        {
            new GraphEdge(new(Guid.NewGuid()), vertices[0].Id, vertices[1].Id, "peer", new Dictionary<string, DatabaseValue>()),
            new GraphEdge(new(Guid.NewGuid()), vertices[1].Id, vertices[2].Id, "other", new Dictionary<string, DatabaseValue>()),
            new GraphEdge(new(Guid.NewGuid()), vertices[0].Id, vertices[2].Id, "peer", new Dictionary<string, DatabaseValue>()),
            new GraphEdge(new(Guid.NewGuid()), vertices[2].Id, vertices[1].Id, "peer", new Dictionary<string, DatabaseValue>())
        };
        var full = GraphReadView.Build(vertices.ToDictionary(v => v.Id), edges.ToDictionary(e => e.Id));
        var compiled = new GraphGqlQueryCompiler().Compile(new(statement));
        var selected = GraphReadView.Build(BoundedGraphPatternExecutor.ExchangeVertices(full, compiled.Query).ToDictionary(v => v.Id),
            edges.Where(e => compiled.Query.Relationships.Any(r => r.Label is null || r.Label == e.Label)).ToDictionary(e => e.Id));
        async Task<string[]> Rows(GraphReadView view)
        {
            var answer = await new BoundedGraphPatternExecutor(null, capturedView: view).ExecuteCapturedResultAsync(compiled.Query,
                new(default, view, view.StatisticsFor(default)), default);
            var json = JsonSerializer.SerializeToElement(GraphPatternQueryResponseMapper.Map(answer, 0), PartitionParticipantWire.Json);
            return json.GetProperty("matches").EnumerateArray().Select(r => r.GetRawText()).Order().ToArray();
        }
        CollectionAssert.AreEqual(await Rows(full), await Rows(selected));
    }

    [TestMethod]
    [DataRow("SELECT ?s WHERE { ?s <urn:p> ?v OPTIONAL { ?s <urn:q> ?q } FILTER NOT EXISTS { ?s <urn:excluded> ?x } }")]
    [DataRow("SELECT ?s WHERE { { ?s <urn:p> ?v } UNION { ?s <urn:q> ?v } MINUS { ?s <urn:excluded> ?x } }")]
    [DataRow("SELECT ?s WHERE { ?s <urn:p> ?v FILTER EXISTS { ?s <urn:excluded> ?x } }")]
    [DataRow("SELECT ?s WHERE { GRAPH <urn:g> { ?s <urn:p> ?v } }")]
    [DataRow("SELECT ?v WHERE { <urn:a> <urn:p>/<urn:q> ?v }")]
    public void Rdf_pruning_preserves_full_relation_answers(string statement)
    {
        RdfQuad[] all = [new(new RdfIri("urn:a"), new("urn:p"), new RdfIri("urn:b")),
            new(new RdfIri("urn:b"), new("urn:q"), new RdfLiteral("value")),
            new(new RdfIri("urn:a"), new("urn:p"), new RdfLiteral("named"), new RdfIri("urn:g")),
            new(new RdfIri("urn:a"), new("urn:excluded"), new RdfLiteral("excluded")),
            new(new RdfIri("urn:noise"), new("urn:noise"), new RdfLiteral("unused"))];
        var patterns = RdfSparql.ExchangePatterns(statement);
        var selected = patterns is null ? all : all.Where(q => patterns.Any(p =>
            (p.Subject is null || p.Subject == q.Subject) && (p.Predicate is null || p.Predicate == q.Predicate) &&
            (p.Object is null || p.Object == q.Object) && (p.Graph is null || p.Graph == q.Graph) &&
            (!p.MatchDefaultGraphOnly || q.Graph is null))).ToArray();
        string[] Rows(RdfQuad[] facts) => ((RdfSparqlSelectResult)RdfSparql.ExecuteRelation(facts, statement, default)).Bindings
            .Select(r => JsonSerializer.Serialize(r, PartitionParticipantWire.Json)).Order().ToArray();
        CollectionAssert.AreEqual(Rows(all), Rows(selected));
    }
}
