using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("LIMIT 2")]
    public async Task Sparql11_construct_blank_labels_are_fresh_per_solution_and_execution(string modifiers)
    {
        string query = "CONSTRUCT { _:item <urn:value> ?x; <urn:self> _:item . _:other <urn:link> _:item } " +
            "WHERE { VALUES ?x { 1 1 2 } } " + modifiers;
        using var store = new VDS.RDF.TripleStore();
        // An explicit non-truncating window avoids dotNetRDF 3.5.2's null
        // reference in its unbounded VALUES-only CONSTRUCT shortcut.
        string referenceQuery = modifiers.Length == 0 ? query + " LIMIT 10000" : query;
        var reference = (VDS.RDF.IGraph)new VDS.RDF.Query.LeviathanQueryProcessor(store)
            .ProcessQuery(new VDS.RDF.Parsing.SparqlQueryParser().ParseFromString(referenceQuery));
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.ExecuteConstruct(database, query);
        int solutions = modifiers.Length == 0 ? 3 : 2;
        Assert.AreEqual(solutions * 3, reference.Triples.Count);
        Assert.AreEqual(reference.Triples.Count, actual.Count);
        Assert.AreEqual(solutions * 2, actual.Select(quad => quad.Subject).Distinct().Count());
        foreach (var self in actual.Where(quad => quad.Predicate.Value == "urn:self"))
        {
            Assert.IsInstanceOfType<RdfBlankNode>(self.Subject);
            Assert.AreEqual(self.Subject, self.Object);
            Assert.AreEqual(1, actual.Count(quad => quad.Predicate.Value == "urn:link" && quad.Object == self.Subject));
        }
        var next = RdfSparql.ExecuteConstruct(database, query);
        Assert.AreEqual(0, actual.Select(quad => quad.Subject).Intersect(next.Select(quad => quad.Subject)).Count());
    }

    [TestMethod]
    public async Task Sparql11_insert_template_blank_nodes_are_atomic_and_delete_labels_reject()
    {
        await using var database = await OpenAsync(CreateDirectory());
        Guid command = Guid.NewGuid();
        const string insert = "INSERT { _:item <urn:value> ?x; <urn:self> _:item } WHERE { VALUES ?x { 1 1 2 } }";
        var first = await database.CommitSparqlUpdateAsync(command, insert);
        var replay = await database.CommitSparqlUpdateAsync(command, insert);
        Assert.AreEqual(first, replay);
        Assert.AreEqual(6, first.MutationCount);
        Assert.AreEqual(3, RdfSparql.Execute(database, "SELECT ?s { ?s <urn:self> ?s }").Count);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "DELETE { _:item <urn:value> ?x } WHERE { ?s <urn:value> ?x }").AsTask());
        Assert.AreEqual(3, RdfSparql.Execute(database, "SELECT ?s { ?s <urn:self> ?s }").Count);
    }
}
