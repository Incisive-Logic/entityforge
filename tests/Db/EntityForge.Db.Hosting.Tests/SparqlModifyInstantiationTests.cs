using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow(false, "entityforgeTemplate0")]
    [DataRow(true, "entityforgeTemplate0")]
    [DataRow(false, "sparql-autos1")]
    [DataRow(true, "sparql-autos1")]
    public async Task Sparql_modify_template_blank_labels_are_fresh_per_solution_without_alias_collisions(bool withUsing, string label)
    {
        string directory = CreateDirectory();
        Guid transaction = Guid.NewGuid();
        string update = (withUsing ? "WITH <urn:target> " : "") + """
            INSERT {
              _:shared <urn:value> ?value .
              GRAPH <urn:named> { _:shared <urn:self> _:shared }
              _:entityforgeTemplate0 <urn:value> "label" .
              [] <urn:value> "anonymous"
            }
            """ + (withUsing ? " USING <urn:source> WHERE { ?s <urn:seed> ?value }" : " WHERE { VALUES ?value { 1 1 } }");
        update = update.Replace("_:entityforgeTemplate0", "_:" + label, StringComparison.Ordinal);
        RdfQuad[] committed;
        await using (var database = await OpenAsync(directory))
        {
            if (withUsing)
            {
                await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { GRAPH <urn:source> { <urn:a> <urn:seed> 1 . <urn:b> <urn:seed> 1 } }");
            }
            await database.CommitSparqlUpdateAsync(transaction, update);
            RdfQuad[] first = database.Find(new()).Where(q => q.Predicate != new RdfIri("urn:seed")).ToArray();
            Assert.HasCount(8, first);
            Assert.HasCount(6, first.Select(q => q.Subject).Distinct().ToArray());
            RdfQuad[] self = first.Where(q => q.Predicate == new RdfIri("urn:self")).ToArray();
            Assert.HasCount(2, self);
            foreach (RdfQuad quad in self)
            {
                Assert.AreEqual(quad.Subject, quad.Object);
                Assert.IsTrue(first.Any(q => q.Subject == quad.Subject && q.Predicate == new RdfIri("urn:value")));
            }
            Assert.HasCount(6, first.Where(q => q.Graph == (withUsing ? new RdfIri("urn:target") : null)).ToArray());
            await database.CommitSparqlUpdateAsync(transaction, update);
            CollectionAssert.AreEquivalent(first, database.Find(new()).Where(q => q.Predicate != new RdfIri("urn:seed")).ToArray());
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update);
            committed = database.Find(new()).ToArray();
            RdfQuad[] twice = committed.Where(q => q.Predicate != new RdfIri("urn:seed")).ToArray();
            Assert.HasCount(16, twice);
            Assert.HasCount(12, twice.Select(q => q.Subject).Distinct().ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        CollectionAssert.AreEquivalent(committed, reopened.Find(new()).ToArray());
    }

    [TestMethod]
    public async Task Sparql_modify_interleaved_graph_templates_preserve_blank_identity_and_lexical_content()
    {
        string directory = CreateDirectory();
        await using var database = await OpenAsync(directory);
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), """
            PREFIX ex: <urn:>
            INSERT {
              _:shared ex:p "before" .
              GRAPH ex:g { _:shared ex:p "GRAPH { quoted }" }
              # Braces and GRAPH in a comment are not syntax: } GRAPH {
              _:shared ex:p "middle" .
              GRAPH ex:h { _:shared ex:p "named" }
              _:shared ex:p "after"
            } WHERE {}
            """);
        RdfQuad[] quads = database.Find(new()).ToArray();
        Assert.HasCount(5, quads);
        Assert.HasCount(1, quads.Select(q => q.Subject).Distinct().ToArray());
        Assert.IsInstanceOfType<RdfBlankNode>(quads[0].Subject);
        Assert.HasCount(3, quads.Where(q => q.Graph is null).ToArray());
        Assert.IsTrue(quads.Any(q => q.Graph == new RdfIri("urn:g") && q.Object == new RdfLiteral("GRAPH { quoted }")));
    }

    [TestMethod]
    [DataRow("{ <urn:s> <urn:p> <urn:o> }")]
    [DataRow("OPTIONAL { <urn:s> <urn:p> <urn:o> }")]
    [DataRow("FILTER(true)")]
    [DataRow("BIND(<urn:o> AS ?o)")]
    public async Task Sparql_modify_template_normalization_does_not_accept_query_clauses(string invalid)
    {
        string directory = CreateDirectory();
        await using var database = await OpenAsync(directory);
        string update = "INSERT { GRAPH <urn:g> { <urn:s> <urn:p> <urn:o> } <urn:s> <urn:p> <urn:o> . " + invalid + " } WHERE {}";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
        Assert.HasCount(0, database.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("INSERT", false)]
    [DataRow("DELETE", false)]
    [DataRow("INSERT", true)]
    [DataRow("DELETE", true)]
    public async Task Sparql_modify_omits_invalid_instantiated_triples_but_applies_valid_siblings(string operation, bool blankPredicate)
    {
        string directory = CreateDirectory();
        var triple = new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfIri("urn:o"));
        var named = triple with
        {
            Graph = new RdfIri("urn:g")
        };
        var constant = new RdfQuad(new RdfIri("urn:constant"), new RdfIri("urn:marker"), new RdfIri("urn:o"));
        var untouched = new RdfQuad(new RdfIri("urn:untouched"), new RdfIri("urn:p"), new RdfLiteral("keep"));
        string where = blankPredicate
            ? "VALUES (?s ?o ?g) { (<urn:s> <urn:o> <urn:g>) } BIND(BNODE('predicate') AS ?p)"
            : """
              VALUES (?s ?p ?o ?g) {
                (<urn:s> <urn:p> <urn:o> <urn:g>)
                (<urn:s> <urn:p> <urn:o> <urn:g>)
                ("literal subject" <urn:p> <urn:o> <urn:g>)
                (UNDEF <urn:p> <urn:o> <urn:g>)
                (<urn:s> "literal predicate" <urn:o> <urn:g>)
                (<urn:s> UNDEF <urn:o> <urn:g>)
                (<urn:s> <urn:p> UNDEF <urn:g>)
                (<urn:s> <urn:p> <urn:o> "literal graph")
                (<urn:s> <urn:p> <urn:o> UNDEF)
              }
              """;
        string update = operation + " { ?s ?p ?o . GRAPH ?g { ?s ?p ?o } <urn:constant> <urn:marker> <urn:o> } WHERE { " + where + " }";
        RdfQuad[] expected = (operation, blankPredicate) switch
        {
            ("INSERT", false) => [triple, named, constant, untouched],
            ("INSERT", true) => [constant, untouched],
            ("DELETE", false) => [untouched],
            _ => [triple, named, untouched]
        };
        await using (var database = await OpenAsync(directory))
        {
            RdfQuad[] seed = operation == "DELETE" ? [triple, named, constant, untouched] : [untouched];
            await database.CommitAsync(new(Guid.NewGuid(), seed.Select(q => (RdfMutation)new AssertRdfQuad(q)).ToArray()));
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Sparql_modify_deletes_all_bound_rows_before_inserting_and_next_operation_reads_final_state(bool namedGraph)
    {
        string directory = CreateDirectory();
        string with = namedGraph ? "WITH <urn:g> " : "";
        string pattern = namedGraph ? "GRAPH <urn:g> { <urn:s> <urn:p> ?value }" : "<urn:s> <urn:p> ?value";
        string update = with + "DELETE { <urn:s> <urn:p> ?value } INSERT { <urn:s> <urn:p> ?next } " +
            "WHERE { <urn:s> <urn:p> ?value BIND(3 - ?value AS ?next) }; " +
            "INSERT { <urn:audit> <urn:count> ?count } WHERE { { SELECT (COUNT(*) AS ?count) WHERE { " + pattern + " } } }";
        RdfIri? graph = namedGraph ? new("urn:g") : null;
        var one = new RdfQuad(new RdfIri("urn:s"), new RdfIri("urn:p"), new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer"), graph);
        var two = new RdfQuad(one.Subject, one.Predicate, new RdfLiteral("2", "http://www.w3.org/2001/XMLSchema#integer"), graph);
        var audit = new RdfQuad(new RdfIri("urn:audit"), new RdfIri("urn:count"), new RdfLiteral("2", "http://www.w3.org/2001/XMLSchema#integer"));
        RdfQuad[] expected = [one, two, audit];
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitAsync(new(Guid.NewGuid(), [new AssertRdfQuad(one), new AssertRdfQuad(two)]));
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
    }
}
