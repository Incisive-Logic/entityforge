using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sparql_anchored_path_join_avoids_a_large_disconnected_scan(bool reverse, bool pathFirst)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = Enumerable.Range(0, 10_100).Select(key => new RdfQuad(new RdfIri("urn:filler:" + key),
            new("urn:key"), new RdfLiteral("filler"))).ToList();
        quads.AddRange(new RdfQuad[]
        {
            new(new RdfIri("urn:a"), new("urn:p"), new RdfIri("urn:b")),
            new(new RdfIri("urn:a"), new("urn:p"), new RdfIri("urn:d")),
            new(new RdfIri("urn:b"), new("urn:p"), new RdfIri("urn:c")),
            new(new RdfIri("urn:d"), new("urn:p"), new RdfIri("urn:c")),
            new(new RdfIri("urn:a"), new("urn:key"), new RdfLiteral("start")),
            new(new RdfIri("urn:c"), new("urn:key"), new RdfLiteral("finish"))
        });
        await database.BulkLoadAsync(Guid.NewGuid(), quads);
        string path = reverse ? "?node <urn:p>/<urn:p> <urn:c>" : "<urn:a> <urn:p>/<urn:p> ?node";
        const string keyPattern = "?node <urn:key> ?value";
        string body = pathFirst ? path + " . " + keyPattern : keyPattern + " . " + path;
        var rows = RdfSparql.Execute(database, "SELECT ?value WHERE { " + body + " }");
        string expected = reverse ? "start" : "finish";
        CollectionAssert.AreEquivalent(new[] { expected, expected },
            rows.Select(row => ((RdfLiteral)row["value"]).LexicalForm).ToArray());
    }

    [TestMethod]
    public async Task Sparql_bound_path_membership_uses_indexed_existence_in_a_large_graph()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var quads = Enumerable.Range(0, 10_100).Select(key => new RdfQuad(new RdfIri("urn:filler:" + key),
            new("urn:unrelated"), new RdfLiteral("value"))).ToList();
        quads.Add(new(new RdfIri("urn:a"), new("urn:p"), new RdfIri("urn:b")));
        quads.Add(new(new RdfIri("urn:c"), new("urn:p"), new RdfLiteral("leaf")));
        await database.BulkLoadAsync(Guid.NewGuid(), quads);
        var results = RdfSparql.Execute(database, "SELECT ?o { VALUES ?s { <urn:a> <urn:a> } ?s <urn:p>* ?o }");
        CollectionAssert.AreEquivalent(new RdfTerm[] { new RdfIri("urn:a"), new RdfIri("urn:b"), new RdfIri("urn:a"), new RdfIri("urn:b") },
            results.Select(row => row["o"]).ToArray());
        var inverse = RdfSparql.Execute(database, "SELECT ?s { VALUES ?o { 'leaf' } ?s <urn:p>* ?o }");
        CollectionAssert.AreEquivalent(new RdfTerm[] { new RdfIri("urn:c"), new RdfLiteral("leaf") }, inverse.Select(row => row["s"]).ToArray());
        Assert.AreEqual(0, RdfSparql.Execute(database, "SELECT ?o { VALUES ?s { <urn:absent> } ?s <urn:p>* ?o }").Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_path_bags_and_reachability_match_an_independent_finite_relation(bool inverseAlternative)
    {
        (int From, int To)[] edges = [(0, 0), (0, 1), (1, 2), (2, 0), (2, 3)];
        var steps = edges.ToList();
        if (inverseAlternative)
        {
            // SPARQL UNION alternatives preserve two derivations of the self-loop.
            // This differs from one undirected GQL traversal of one physical edge.
            steps.AddRange(edges.Select(edge => (edge.To, edge.From)));
        }
        await using var database = await OpenAsync(CreateDirectory());
        RdfIri Node(int key) => new("urn:node:" + key);
        await database.CommitAsync(new(Guid.NewGuid(), edges.Select(edge =>
            (RdfMutation)new AssertRdfQuad(new(Node(edge.From), new("urn:route"), Node(edge.To)))).ToArray()));
        string step = inverseAlternative ? "(<urn:route>|^<urn:route>)" : "<urn:route>";
        string Key((int From, int To) pair) => Node(pair.From).Value + "," + Node(pair.To).Value;
        string[] Actual(string path) => RdfSparql.Execute(database, "SELECT ?s ?o WHERE { ?s " + path + " ?o }")
            .Select(row => ((RdfIri)row["s"]).Value + "," + ((RdfIri)row["o"]).Value).ToArray();
        var walks = Enumerable.Range(0, 4).Select(key => (From: key, To: key)).ToList();
        for (int length = 1; length <= 4; length++)
        {
            walks = (from walk in walks
                     from edge in steps
                     where walk.To == edge.From
                     select (walk.From, edge.To)).ToList();
            CollectionAssert.AreEquivalent(walks.Select(Key).ToArray(), Actual(String.Join("/", Enumerable.Repeat(step, length))),
                "Fixed-length path composition must preserve each derivation.");
        }
        var reachability = new HashSet<(int From, int To)>(steps);
        bool changed;
        do
        {
            changed = false;
            foreach (var pair in reachability.ToArray())
            {
                foreach (var edge in steps.Where(edge => edge.From == pair.To))
                {
                    changed |= reachability.Add((pair.From, edge.To));
                }
            }
        } while (changed);
        CollectionAssert.AreEquivalent(reachability.Select(Key).ToArray(), Actual(step + "+"));
        var reflexive = reachability.Concat(Enumerable.Range(0, 4).Select(key => (From: key, To: key))).Distinct().ToArray();
        CollectionAssert.AreEquivalent(reflexive.Select(Key).ToArray(), Actual(step + "*"));
        var composed = (from left in reachability
                        from right in reachability
                        where left.To == right.From
                        select (left.From, right.To)).ToArray();
        CollectionAssert.AreEquivalent(composed.Select(Key).ToArray(), Actual(step + "+/" + step + "+"),
            "Distinct reachability operands still produce a bag when composed through different intermediate nodes.");
    }
}
