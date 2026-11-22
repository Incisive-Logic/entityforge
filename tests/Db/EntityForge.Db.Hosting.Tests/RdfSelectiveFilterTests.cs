using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfSelectiveFilterTests
{
    private const int Subjects = 10032;
    private const string IntegerType = "http://www.w3.org/2001/XMLSchema#integer";

    [TestMethod]
    public void Selective_star_filters_before_the_intermediate_row_bound()
    {
        var facts = Enumerable.Range(0, Subjects).SelectMany(key => new RdfQuad[]
        {
            new(new RdfIri("urn:item:" + key), new("urn:type"), new RdfIri("urn:item")),
            new(new RdfIri("urn:item:" + key), new("urn:number"), new RdfLiteral(key.ToString(), datatype: IntegerType)),
            new(new RdfIri("urn:item:" + key), new("urn:name"), new RdfLiteral("item-" + key))
        }).ToArray();
        var result = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(facts,
            "SELECT ?number ?name WHERE { ?root <urn:type> <urn:item> . ?root <urn:number> ?number . ?root <urn:name> ?name . FILTER(?number >= 10020 && ?number <= 10027) }", default);
        Assert.AreEqual(8, result.Bindings.Count);
        CollectionAssert.AreEquivalent(Enumerable.Range(10020, 8).Select(key => "item-" + key).ToArray(),
            result.Bindings.Select(row => ((RdfLiteral)row["name"]).LexicalForm).ToArray());
    }

    [TestMethod]
    public void Unselective_star_still_enforces_the_intermediate_row_bound()
    {
        var facts = Enumerable.Range(0, Subjects).Select(key => new RdfQuad(new RdfIri("urn:item:" + key),
            new("urn:number"), new RdfLiteral(key.ToString(), datatype: IntegerType))).ToArray();
        Assert.ThrowsException<RdfSparqlWorkLimitException>(() => RdfSparql.ExecuteRelation(facts,
            "SELECT ?number WHERE { ?root <urn:number> ?number . FILTER(?number >= 0) }", default));
    }

    [TestMethod]
    public void Disjunction_is_not_split_into_rejecting_conjuncts()
    {
        RdfQuad[] facts = [new(new RdfIri("urn:item"), new("urn:number"), new RdfLiteral("2", datatype: IntegerType))];
        var result = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(facts,
            "SELECT ?number WHERE { ?root <urn:number> ?number . FILTER(?number = 1 || ?number = 2) }", default);
        Assert.AreEqual(1, result.Bindings.Count);
    }
}
