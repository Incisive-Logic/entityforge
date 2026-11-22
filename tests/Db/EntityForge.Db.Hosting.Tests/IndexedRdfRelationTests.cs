using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class IndexedRdfRelationTests
{
    [TestMethod]
    public void Indexed_relation_matches_scan_for_terms_graphs_prefixes_and_order()
    {
        RdfTerm[] subjects = [new RdfIri("urn:s:1"), new RdfBlankNode("s2")];
        RdfIri[] predicates = [new("urn:p:1"), new("urn:p:2")];
        RdfTerm[] values = [new RdfLiteral("1"), new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer"),
            new RdfLiteral("hello", language: "en"), new RdfIri("urn:value")];
        RdfTerm?[] graphs = [null, new RdfIri("urn:g:1"), new RdfBlankNode("g2")];
        var relation = (from subject in subjects
                        from predicate in predicates
                        from value in values
                        from graph in graphs
                        select new RdfQuad(subject, predicate, value, graph)).ToArray();
        var index = new IndexedRdfRelation(relation);
        using var indexed = index.Snapshot(new(7));
        using var scan = new RelationRdfSparqlDatasetSnapshot(relation.ToArray(), new(7));
        relation[0] = new(new RdfIri("urn:mutated"), predicates[0], values[0]);
        foreach (var subject in subjects.Cast<RdfTerm?>().Prepend(null).Append(new RdfIri("urn:missing")))
        {
            foreach (var predicate in predicates.Cast<RdfIri?>().Prepend(null))
            {
                foreach (var value in values.Cast<RdfTerm?>().Prepend(null))
                {
                    foreach (var graph in graphs)
                    {
                        foreach (bool defaults in new[] { false, true })
                        {
                            if (defaults && graph is not null)
                            {
                                continue;
                            }
                            var pattern = new RdfQuadPattern(subject, predicate, value, graph, defaults);
                            CollectionAssert.AreEqual(scan.Find(pattern).ToArray(), indexed.Find(pattern).ToArray());
                            Assert.AreEqual(scan.EstimateCandidates(pattern), indexed.EstimateCandidates(pattern));
                            foreach (int maximum in new[] { 0, 1, 99 })
                            {
                                foreach (bool namedOnly in new[] { false, true })
                                {
                                    CollectionAssert.AreEqual(scan.ReadPrefix(pattern, maximum, namedOnly, default).ToArray(),
                                        indexed.ReadPrefix(pattern, maximum, namedOnly, default).ToArray());
                                }
                            }
                        }
                    }
                }
            }
        }

        foreach (int maximum in new[] { 0, 1, 99 })
        {
            CollectionAssert.AreEqual(scan.ReadNamedGraphNames(maximum, default).ToArray(), indexed.ReadNamedGraphNames(maximum, default).ToArray());
        }
        Assert.AreEqual(new DatabaseVersion(8), index.Snapshot(new(8)).Version);
    }

    [TestMethod]
    [DataRow("SELECT ?s ?o WHERE { ?s <urn:p> ?o FILTER(?o = 1) } ORDER BY ?s ?o")]
    [DataRow("SELECT ?s ?o ?other WHERE { ?s <urn:p> ?o OPTIONAL { ?s <urn:other> ?other } } ORDER BY ?s ?o ?other")]
    [DataRow("SELECT ?s ?o WHERE { ?s <urn:p> ?o . ?s <urn:other> ?other FILTER(?o = ?other) } ORDER BY ?s ?o")]
    [DataRow("SELECT ?g (COUNT(*) AS ?n) WHERE { GRAPH ?g { ?s ?p ?o } } GROUP BY ?g ORDER BY ?g")]
    [DataRow("SELECT ?s WHERE { { ?s <urn:p> ?v } UNION { ?s <urn:other> ?v } } ORDER BY ?s")]
    [DataRow("ASK { GRAPH <urn:graph> { ?s <urn:p> ?o } }")]
    public void Indexed_sparql_evaluation_matches_scan(string query)
    {
        var s = new RdfIri("urn:s");
        var p = new RdfIri("urn:p");
        var number = new RdfLiteral("01", "http://www.w3.org/2001/XMLSchema#integer");
        RdfQuad[] relation = [new(s, p, number), new(s, new("urn:other"), number),
            new(new RdfIri("urn:second"), p, new RdfLiteral("1")), new(s, p, number, new RdfIri("urn:graph"))];
        using var scan = new RelationRdfSparqlDatasetSnapshot(relation, new(1));
        using var indexed = new IndexedRdfRelation(relation).Snapshot(new(1));
        var expected = RdfSparql.ExecuteDataset(scan, query, default);
        var actual = RdfSparql.ExecuteDataset(indexed, query, default);
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(expected, expected.GetType()),
            System.Text.Json.JsonSerializer.Serialize(actual, actual.GetType()));
    }

    [TestMethod]
    public async Task Cancelled_and_parallel_readers_do_not_poison_shared_indexes()
    {
        var p = new RdfIri("urn:p");
        var rows = Enumerable.Range(0, 1000).Select(i => new RdfQuad(new RdfIri("urn:s:" + i), p, new RdfLiteral(i.ToString()))).ToArray();
        var index = new IndexedRdfRelation(rows);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var snapshot = index.Snapshot(new(1));
        Assert.ThrowsExactly<OperationCanceledException>(() => snapshot.ReadPrefix(new(Predicate: p), 1, false, cancelled.Token));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            using var reader = index.Snapshot(new(1));
            CollectionAssert.AreEqual(new[] { rows[i] }, reader.Find(new(Object: rows[i].Object)).ToArray());
            Assert.AreEqual(1000, reader.Find(new(Predicate: p)).Count);
        })));
    }

    [TestMethod]
    public void Oversized_relation_uses_complete_scan_fallback()
    {
        var p = new RdfIri("urn:p");
        var rows = Enumerable.Range(0, IndexedRdfRelation.MaximumIndexedQuads + 1)
            .Select(i => new RdfQuad(new RdfIri("urn:s:" + i), p, new RdfLiteral("value"))).ToArray();
        using var snapshot = new IndexedRdfRelation(rows).Snapshot(new(1));
        CollectionAssert.AreEqual(new[] { rows[^1] }, snapshot.Find(new(Subject: rows[^1].Subject)).ToArray());
        Assert.AreEqual(0, snapshot.ReadNamedGraphNames(1, default).Count);
    }
}
