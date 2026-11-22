using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("BASE # base comment\n<https://example.test/> PREFIX ex: <model/>")]
    [DataRow("BASE <https://example.test/> PREFIX # prefix comment\nex: <model/>")]
    [DataRow("BASE <https://example.test/> PREFIX ex: # namespace comment\n<model/>")]
    [DataRow("BASE # base comment\r<https://example.test/> PREFIX ex: <model/>")]
    [DataRow("BASE <https://example.test/> PREFIX # prefix comment\rex: <model/>")]
    [DataRow("BASE <https://example.test/> PREFIX ex: # namespace comment\r<model/>")]
    [DataRow("BASE # base comment\r\n<https://example.test/> PREFIX ex: <model/>")]
    [DataRow("BASE <https://example.test/> PREFIX # prefix comment\r\nex: <model/>")]
    [DataRow("BASE <https://example.test/> PREFIX ex: # namespace comment\r\n<model/>")]
    public async Task Sparql_update_prolog_comments_preserve_sequence_inheritance(string prolog)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), prolog +
            " INSERT DATA { ex:s ex:p 1 }; INSERT { ex:s ex:q ?v } WHERE { ex:s ex:p ?v }");
        var one = new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer");
        RdfQuad[] expected =
        [
            new(new RdfIri("https://example.test/model/s"), new RdfIri("https://example.test/model/p"), one),
            new(new RdfIri("https://example.test/model/s"), new RdfIri("https://example.test/model/q"), one)
        ];
        CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        var receipt = await database.CommitSparqlUpdateAsync(Guid.NewGuid(), prolog);
        Assert.AreEqual(0, receipt.MutationCount);
        CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("PREFIXex: <urn:bad:>")]
    [DataRow("PREFIX # missing namespace")]
    [DataRow("BASE # missing IRI")]
    public async Task Sparql_update_prolog_comments_do_not_hide_incomplete_declarations(string prolog)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(), prolog).AsTask());
        Assert.HasCount(0, database.Find(new()).ToArray());
    }

    [TestMethod]
    public async Task Sparql_update_prefix_adaptation_preserves_literals_datatypes_and_existing_names()
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), """
            PREFIX 𐀀: <urn:unicode:>
            PREFIX entityforgePrefix0: <urn:occupied:>
            INSERT DATA {
              𐀀:s 𐀀:p '𐀀:literal', "𐀀:literal", '''𐀀:long
            literal''', "typed"^^𐀀:type, entityforgePrefix0:item .
              # 𐀀:comment is not a term
            }
            """);
        RdfTerm[] terms = [new RdfLiteral("𐀀:literal"), new RdfLiteral("𐀀:long\nliteral"),
            new RdfLiteral("typed", "urn:unicode:type"), new RdfIri("urn:occupied:item")];
        CollectionAssert.AreEquivalent(terms.Select(term => new RdfQuad(new RdfIri("urn:unicode:s"), new RdfIri("urn:unicode:p"), term)).ToArray(),
            database.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("𐀀.")]
    [DataRow("1𐀀")]
    [DataRow("\u0301𐀀")]
    public async Task Sparql_update_prefix_adaptation_does_not_accept_invalid_prefix_names(string prefix)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "PREFIX " + prefix + ": <urn:invalid:> INSERT DATA { " + prefix + ":s <urn:p> 1 }").AsTask());
        Assert.HasCount(0, database.Find(new()).ToArray());
    }

    [TestMethod]
    [DataRow("é")]
    [DataRow("a.b")]
    [DataRow("a\u0301")]
    [DataRow("\U00010000")]
    public async Task Sparql_update_prefix_grammar_is_shared_across_commands_and_sequence_inheritance(string prefix)
    {
        string directory = CreateDirectory();
        string p = prefix + ":";
        string update = "PREFIX " + p + " <urn:first:> INSERT DATA { GRAPH " + p + "g { " + p + "s " + p + "p 1 } }; " +
            "WITH " + p + "g INSERT { " + p + "s " + p + "q ?v } WHERE { " + p + "s " + p + "p ?v }; " +
            "COPY GRAPH " + p + "g TO DEFAULT; " +
            "PREFIX " + p + " <urn:second:> INSERT DATA { " + p + "s " + p + "p 2 }";
        var one = new RdfLiteral("1", "http://www.w3.org/2001/XMLSchema#integer");
        RdfQuad[] expected =
        [
            new(new RdfIri("urn:first:s"), new RdfIri("urn:first:p"), one),
            new(new RdfIri("urn:first:s"), new RdfIri("urn:first:q"), one),
            new(new RdfIri("urn:first:s"), new RdfIri("urn:first:p"), one, new RdfIri("urn:first:g")),
            new(new RdfIri("urn:first:s"), new RdfIri("urn:first:q"), one, new RdfIri("urn:first:g")),
            new(new RdfIri("urn:second:s"), new RdfIri("urn:second:p"), new RdfLiteral("2", "http://www.w3.org/2001/XMLSchema#integer"))
        ];
        await using (var database = await OpenAsync(directory))
        {
            await database.CommitSparqlUpdateAsync(Guid.NewGuid(), update);
            CollectionAssert.AreEquivalent(expected, database.Find(new()).ToArray());
        }
        await using var reopened = await OpenAsync(directory);
        CollectionAssert.AreEquivalent(expected, reopened.Find(new()).ToArray());
    }
}
