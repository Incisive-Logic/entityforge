using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("INSERT DATA {}; INSERT { <urn:new> <urn:p> 7 } WHERE {}; DELETE DATA {}")]
    [DataRow("INSERT DATA { <urn:new> <urn:p> '''a;{b}''' }; INSERT { ?s <urn:q> ?v } WHERE { ?s <urn:p> ?v }; # trailing comment")]
    [DataRow("INSERT DATA { <urn:new> <urn:p> 7 }; INSERT { ?s <urn:q> ?v } WHERE { ?s <urn:p> ?v }; DELETE WHERE { ?s <urn:p> ?v }")]
    [DataRow("PREFIX ex:<urn:> INSERT DATA { ex:new ex:p 'a;{b}' }; INSERT { ?s ex:q ?v } WHERE { ?s ex:p ?v }; CLEAR DEFAULT")]
    [DataRow("COPY GRAPH <urn:g> TO DEFAULT; INSERT { ?s <urn:q> ?v } WHERE { ?s <urn:p> ?v }; CLEAR GRAPH <urn:g>")]
    [DataRow("WITH <urn:g> DELETE { ?s <urn:p> ?v } INSERT { ?s <urn:q> ?v } WHERE { ?s <urn:p> ?v }")]
    [DataRow("WITH <urn:new> INSERT { <urn:s> <urn:p> 'new' } WHERE {}")]
    [DataRow("WITH <urn:g> INSERT { ?s <urn:copy> ?v } WHERE { GRAPH <urn:h> { ?s <urn:p> ?v } }")]
    [DataRow("WITH <urn:g> INSERT { GRAPH <urn:explicit> { ?s <urn:copy> ?v } } WHERE { ?s <urn:p> ?v }")]
    [DataRow("INSERT { ?s <urn:copy> ?v } USING <urn:g> WHERE { ?s <urn:p> ?v }")]
    [DataRow("INSERT { ?s <urn:copy> ?v } USING <urn:g> USING <urn:h> WHERE { ?s <urn:p> ?v }")]
    [DataRow("WITH <urn:target> INSERT { ?s <urn:copy> ?v } USING <urn:g> WHERE { ?s <urn:p> ?v }")]
    [DataRow("WITH <urn:target> INSERT { ?s <urn:copy> ?v } USING NAMED <urn:g> WHERE { GRAPH ?g { ?s <urn:p> ?v } }")]
    [DataRow("PREFIX ex:<urn:> WITH ex:g DELETE { ?s ex:p ?v } WHERE { ?s ex:p ?v }")]
    [DataRow("INSERT { GRAPH ?missing { ?s <urn:copy> ?v } } WHERE { ?s <urn:p> ?v }")]
    public async Task Sparql11_modify_datasets_match_reference_and_replay_atomically(string update)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(DatasetSeed, "modify-datasets")));
        using var reference = new VDS.RDF.TripleStore();
        var processor = new VDS.RDF.Update.LeviathanUpdateProcessor(reference);
        var parser = new VDS.RDF.Parsing.SparqlUpdateParser();
        processor.ProcessCommandSet(parser.ParseFromString(DatasetSeed));
        // The reference parser mishandles insert-only WITH ... USING. An empty
        // DELETE selects its equivalent combined-modification grammar branch.
        string referenceUpdate = update.StartsWith("WITH", StringComparison.Ordinal) && update.Contains("USING", StringComparison.Ordinal)
            ? update.Replace(" INSERT ", " DELETE {} INSERT ", StringComparison.Ordinal) : update;
        processor.ProcessCommandSet(parser.ParseFromString(referenceUpdate));
        var transaction = Guid.NewGuid();
        var receipt = await database.CommitSparqlUpdateAsync(transaction, update);
        Assert.AreEqual(receipt, await database.CommitSparqlUpdateAsync(transaction, update));
        string[] expected = reference.Graphs.SelectMany(graph => graph.Triples.Select(triple => new RdfQuad(
            RdfSyntax.FromNode(triple.Subject, "reference"), (RdfIri)RdfSyntax.FromNode(triple.Predicate, "reference"),
            RdfSyntax.FromNode(triple.Object, "reference"), graph.Name is null ? null : RdfSyntax.FromNode(graph.Name, "reference")).ToString())).ToArray();
        CollectionAssert.AreEquivalent(expected, database.Find(new()).Select(quad => quad.ToString()).ToArray());
    }

    [TestMethod]
    public async Task Sparql11_update_sequence_inherits_resolved_prologs_and_scopes_data_blank_nodes()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "BASE <http://example.org/a/> INSERT DATA { <s> <p> 1 }; BASE <http://example.org/b/> INSERT { <s> <q> ?o } WHERE { <http://example.org/a/s> <http://example.org/a/p> ?o }");
        Assert.IsTrue(database.Find(new()).Any(q => q.Subject == new RdfIri("http://example.org/b/s") && q.Predicate.Value == "http://example.org/b/q"));
        var data = RdfSparqlUpdate.Parse(
            "PREFIX ex:<urn:first:> INSERT DATA { ex:s ex:p 1 }; PREFIX ex:<urn:second:> INSERT DATA { ex:s ex:p 2 }", "prefix-data");
        CollectionAssert.AreEquivalent(new[] { "urn:first:p", "urn:second:p" }, data.Cast<AssertRdfQuad>().Select(m => m.Quad.Predicate.Value).ToArray());
        const string reused = "INSERT DATA { _:b <urn:p> 1 }; INSERT {} WHERE {}; INSERT DATA { _:b <urn:q> 2 }";
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparqlUpdate.Parse(reused, "invalid"));
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparqlUpdate.ParseWhere(Array.Empty<RdfQuad>(), reused, "invalid", default));
        var anonymous = RdfSparqlUpdate.Parse("INSERT DATA { [] <urn:p> '_:b' }; INSERT DATA { [] <urn:p> '_:b' }", "anonymous");
        Assert.AreEqual(2, anonymous.Cast<AssertRdfQuad>().Select(m => m.Quad.Subject).Distinct().Count());
    }

    [TestMethod]
    public async Task Sparql11_sequence_failure_leaves_no_partial_writes()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string update = "INSERT DATA { <urn:new> <urn:p> 7 }; COPY GRAPH <urn:missing> TO DEFAULT";
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), update).AsTask());
        Assert.AreEqual(0, database.Find(new()).Count);
        var sequence = "INSERT DATA { <urn:new> <urn:p> 7 }; INSERT { ?s <urn:q> ?v } WHERE { ?s <urn:p> ?v }";
        var mutations = RdfSparqlUpdate.ParseWhere(Array.Empty<RdfQuad>(), sequence, "sequence", default);
        Assert.AreEqual(2, mutations.Count);
        Assert.IsTrue(mutations.Cast<AssertRdfQuad>().Any(m => m.Quad.Predicate.Value == "urn:q"));
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "PREFIX ex:<urn:> INSERT DATA { ex:new ex:p 7 }; PREFIX ex:<urn:other:> INSERT { ex:s ex:q ?v } WHERE { ?s <urn:p> ?v }");
        Assert.IsTrue(database.Find(new()).Any(q => q.Predicate.Value == "urn:other:q"));
    }

    [TestMethod]
    public async Task Sparql11_modify_templates_share_one_where_solution_sequence()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string update = "DELETE { ?id <urn:p> ?value } INSERT { ?id <urn:q> ?value } WHERE { BIND(UUID() AS ?id) BIND(RAND() AS ?value) }";
        var mutations = RdfSparqlUpdate.ParseWhere(database, update, "same-solutions");
        Assert.AreEqual(2, mutations.Count);
        var removed = (RetractRdfQuad)mutations[0];
        var added = (AssertRdfQuad)mutations[1];
        Assert.AreEqual(removed.Quad.Subject, added.Quad.Subject);
        Assert.AreEqual(removed.Quad.Object, added.Quad.Object);
    }

    [TestMethod]
    public async Task Sparql11_using_named_replaces_with_default_dataset()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitAsync(new(Guid.NewGuid(), RdfSparqlUpdate.Parse(DatasetSeed, "using-named")));
        // SPARQL Update 3.1.3: either USING form overrides WITH for evaluating WHERE.
        // The reference processor incorrectly retains WITH's default for NAMED-only datasets.
        var result = await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "WITH <urn:g> INSERT { ?s <urn:copy> ?v } USING NAMED <urn:h> WHERE { ?s <urn:p> ?v }");
        Assert.AreEqual(0, result.MutationCount);
        Assert.AreEqual(5, database.Find(new()).Count);
    }

    [TestMethod]
    public async Task Sparql11_update_dispatch_preserves_comments_and_data_commands()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var data = RdfSparqlUpdate.Parse("# header\nINSERT # data\nDATA { GRAPH <urn:g> { <urn:s> <urn:p> 1 } }", "commented");
        await database.CommitAsync(new(Guid.NewGuid(), data));
        const string update = "# header\nPREFIX ex:<urn:> # prefix\nWITH ex:g INSERT { ?s ex:q ?v } USING ex:g WHERE { ?s ex:p ?v }";
        Assert.ThrowsExactly<NotSupportedException>(() => RdfSparqlUpdate.Parse(update, "commented"));
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update);
        Assert.AreEqual(1, database.Find(new(Predicate: new RdfIri("urn:q"), Graph: new RdfIri("urn:g"))).Count);
    }
}
