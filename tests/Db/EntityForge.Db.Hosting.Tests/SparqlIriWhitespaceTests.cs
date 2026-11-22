using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    public void Sparql_data_iri_binding_preserves_update_size_budget_and_predicate_shorthand()
    {
        string lexical = new('x', 300_000);
        var mutations = RdfSparqlUpdate.Parse("INSERT DATA { <urn:s> a \"" + lexical + "\" }", "scope");
        Assert.HasCount(1, mutations);
        Assert.AreEqual(new RdfIri("http://www.w3.org/1999/02/22-rdf-syntax-ns#type"), mutations[0].Quad.Predicate);
        Assert.AreEqual(new RdfLiteral(lexical), mutations[0].Quad.Object);
    }

    [TestMethod]
    [DataRow("INSERT DATA { a <urn:p> <urn:o> }")]
    [DataRow("INSERT DATA { <urn:s> <urn:p> a }")]
    [DataRow("INSERT DATA { GRAPH a { <urn:s> <urn:p> <urn:o> } }")]
    public void Sparql_data_iri_binding_does_not_expand_predicate_shorthand_in_other_positions(string update)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparqlUpdate.Parse(update, "scope"));
    }

    [TestMethod]
    [DataRow("https://EXAMPLE.test/a/../b")]
    [DataRow("https://example.test/\u00A0;#graph")]
    public async Task Sparql_updates_preserve_graph_identity_across_data_modify_and_management(string graph)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string name = "<" + graph + ">";
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { GRAPH " + name + " { <urn:s> <urn:p> 1 } }");
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT { GRAPH " + name +
            " { <urn:s> <urn:q> ?v } } USING " + name + " WHERE { <urn:s> <urn:p> ?v }");
        Assert.HasCount(2, database.Find(new(Graph: new RdfIri(graph))).ToArray());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "COPY GRAPH " + name + " TO DEFAULT; CLEAR GRAPH " + name);
        Assert.HasCount(0, database.Find(new(Graph: new RdfIri(graph))).ToArray());
        Assert.HasCount(2, database.Find(new(MatchDefaultGraphOnly: true)).ToArray());
        var load = RdfSparqlUpdate.ParseLoads("LOAD <https://example.test/source> INTO GRAPH " + name, "scope").Single();
        Assert.AreEqual(new RdfIri(graph), load.Graph);
        Assert.AreEqual("https://example.test/source", load.Source);
    }

    [TestMethod]
    [DataRow("https://EXAMPLE.test/a/../b", "https://example.test/b")]
    [DataRow("urn:raw:\u00A0", "urn:raw:%C2%A0")]
    [DataRow("https://example.test/a%2fb", "https://example.test/a%2Fb")]
    [DataRow("urn:entityforge:data-iri:0", "urn:entityforge:data-iri:1")]
    [DataRow("urn:entityforge:predicate-shorthand", "http://www.w3.org/1999/02/22-rdf-syntax-ns#type")]
    [DataRow("c:/data", "http://[v1.a]:99999/")]
    public void Sparql_data_preserves_distinct_iri_spellings_in_all_term_positions(string first, string second)
    {
        string Triples(string iri) => "GRAPH <" + iri + "> { <" + iri + "> <" + iri + "> <" + iri +
            ">, \"literal <urn:unchanged>\"^^<" + iri + "> }";
        string data = " { " + Triples(first) + " " + Triples(second) + " }";
        RdfQuad[] expected = new[] { first, second }.SelectMany(value =>
        {
            var iri = new RdfIri(value);
            return new[]
            {
                new RdfQuad(iri, iri, iri, iri),
                new RdfQuad(iri, iri, new RdfLiteral("literal <urn:unchanged>", value), iri)
            };
        }).ToArray();
        foreach (string command in new[] { "INSERT DATA", "DELETE DATA" })
        {
            var mutations = RdfSparqlUpdate.Parse(command + data, "test");
            CollectionAssert.AreEquivalent(expected, mutations.Select(mutation => mutation.Quad).ToArray());
        }
    }

    [TestMethod]
    [DataRow("\u00A0", false)]
    [DataRow("\u2003", false)]
    [DataRow("\u2028", false)]
    [DataRow("\u3000", false)]
    [DataRow("\u00A0", true)]
    [DataRow("\u2003", true)]
    [DataRow("\u2028", true)]
    [DataRow("\u3000", true)]
    public async Task Sparql_iri_unicode_spacing_characters_preserve_identity(string character, bool escaped)
    {
        await using var database = await OpenAsync(CreateDirectory());
        string spelling = escaped ? "\\u" + ((int)character[0]).ToString("X4", System.Globalization.CultureInfo.InvariantCulture) : character;
        string iri = "https://example.test/" + character + "item";
        string prolog = "BASE <https://example.test/> PREFIX ex: <" + spelling + "> ";
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), prolog +
            "INSERT DATA { <" + spelling + "item> <urn:p> 1 }; INSERT DATA { ex:item <urn:q> 2 }");
        Assert.HasCount(2, database.Find(new()).ToArray());
        Assert.IsTrue(database.Find(new()).All(quad => quad.Subject == new RdfIri(iri)), String.Join(" | ", database.Find(new()).Select(quad => ((RdfIri)quad.Subject).Value)));
        var result = (RdfSparqlSelectResult)await database.QuerySparqlAsync(prolog +
            "SELECT ?s WHERE { ?s <urn:p> 1 FILTER(sameTerm(?s, ex:item)) }");
        Assert.HasCount(1, result.Bindings);
        Assert.AreEqual(new RdfIri(iri), result.Bindings[0]["s"]);
    }
}
