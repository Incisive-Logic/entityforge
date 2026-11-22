using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfLiteralPrefixTests
{
    [TestMethod]
    [DataRow("\"0:-1:payload\"")]
    [DataRow("\"0:-1:payload\"@en")]
    [DataRow("\"0:-1:payload\"^^<http://www.w3.org/2001/XMLSchema#string>")]
    [DataRow("\"\"\"0:-1:payload\"\"\"")]
    public void Construct_preserves_colons_inside_literals_and_expands_predicate_prefix(string literal)
    {
        RdfQuad[] facts = [new(new RdfIri("urn:item"), new("urn:name"), new RdfLiteral("old"))];
        var result = (RdfSparqlGraphResult)RdfSparql.ExecuteRelation(facts,
            $"PREFIX w: <urn:> CONSTRUCT {{ ?s w:name {literal} }} WHERE {{ ?s w:name ?old }}", default);

        Assert.AreEqual(1, result.Quads.Count);
        Assert.AreEqual(new RdfIri("urn:name"), result.Quads[0].Predicate);
        Assert.AreEqual("0:-1:payload", ((RdfLiteral)result.Quads[0].Object).LexicalForm);
    }

    [TestMethod]
    public void Undeclared_predicate_prefix_is_still_rejected()
    {
        var error = Assert.ThrowsExactly<InvalidDataException>(() => RdfSparql.ExecuteRelation([],
            "SELECT ?s WHERE { ?s missing:name \"0:-1:payload\" }", default));
        Assert.AreEqual("SPARQL prefix 'missing:' is not declared.", error.Message);
    }
}
