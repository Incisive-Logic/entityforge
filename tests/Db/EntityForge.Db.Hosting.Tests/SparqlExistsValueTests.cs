using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("?s <urn:p> ?o BIND(EXISTS { ?s <urn:p> ?o } AS ?value)", "true")]
    [DataRow("?s <urn:p> ?o BIND(?o+1 AS ?value)", "2")]
    [DataRow("{ SELECT (EXISTS { <urn:a> <urn:p> ?o } AS ?value) WHERE {} } BIND(<urn:a> AS ?s)", "true")]
    [DataRow("?s <urn:p> ?o BIND('WHERE { }' AS ?value)", "WHERE { }")]
    public async Task Sparql11_modify_WHERE_preserves_native_expressions(string where, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT DATA { <urn:a> <urn:p> 1 }");
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(), "INSERT { ?s <urn:result> ?value } WHERE { " + where + " }");
        Assert.AreEqual(expected, ((RdfLiteral)database.Find(new()).Single(quad => quad.Predicate.Value == "urn:result").Object).LexicalForm);
    }

    [TestMethod]
    [DataRow("{} LIMIT 1")]
    [DataRow("{} ORDER BY ?s")]
    [DataRow("{} VALUES ?s { <urn:a> }")]
    [DataRow("{ BIND(1 AS ?x) ")]
    public void Sparql11_modify_WHERE_rejects_unclosed_groups_and_query_modifiers(string group)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => RdfSparqlUpdate.ParseWhere([], "INSERT { <urn:a> <urn:p> 1 } WHERE " + group, "invalid", default));
    }

    [TestMethod]
    [DataRow("SELECT (EXISTS { ?s <urn:p> ?o } AS ?value) FROM <urn:g> WHERE { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "false,true")]
    [DataRow("SELECT (EXISTS { ?s <urn:p> ?o } AS ?value) WHERE { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "true,false")]
    [DataRow("SELECT ?value WHERE { VALUES ?s { <urn:a> <urn:b> } BIND(EXISTS { ?s <urn:p> ?o } AS ?value) } ORDER BY ?s", "true,false")]
    [DataRow("SELECT (IF(EXISTS { ?s <urn:p> ?o },1,0) AS ?value) WHERE { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "1,0")]
    [DataRow("SELECT (STR(EXISTS { ?s <urn:p> ?o }) AS ?value) WHERE { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "true,false")]
    [DataRow("SELECT (COALESCE(1/0, NOT EXISTS { ?s <urn:p> ?o }) AS ?value) WHERE { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "false,true")]
    [DataRow("SELECT (SUM(IF(EXISTS { ?s <urn:p> ?o },1,0)) AS ?value) WHERE { VALUES ?s { <urn:a> <urn:b> } }", "1")]
    [DataRow("SELECT ?value WHERE { VALUES ?s { <urn:a> <urn:b> } } GROUP BY (EXISTS { ?s <urn:p> ?o } AS ?value) ORDER BY ?value", "false,true")]
    [DataRow("SELECT (STR(?s) AS ?value) WHERE { VALUES ?s { <urn:b> <urn:a> } } ORDER BY DESC(EXISTS { ?s <urn:p> ?o })", "urn:a,urn:b")]
    [DataRow("SELECT (STR(?s) AS ?value) WHERE { VALUES ?s { <urn:b> <urn:a> } FILTER(IF(EXISTS { ?s <urn:p> ?o },true,false)) }", "urn:a")]
    [DataRow("SELECT ?value WHERE { GRAPH <urn:g> { VALUES ?s { <urn:a> <urn:b> } BIND(EXISTS { ?s <urn:p> ?o } AS ?value) } } ORDER BY ?s", "false,true")]
    [DataRow("SELECT ?value WHERE { { SELECT (EXISTS { ?s <urn:p> ?o } AS ?value) WHERE { VALUES ?s { <urn:a> } } } }", "true")]
    [DataRow("SELECT (IF(true,7,IF(EXISTS { ?s <urn:p> ?o },1/0,2)) AS ?value) {}", "7")]
    [DataRow("SELECT (EXISTS { OPTIONAL { ?s <urn:p> ?o } FILTER(BOUND(?o)) } AS ?value) { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "true,false")]
    [DataRow("SELECT (EXISTS { BIND(STR(?s) AS ?local) FILTER(?local='urn:a') } AS ?value) { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "true,false")]
    [DataRow("SELECT (EXISTS { VALUES ?local { 'default' 'missing' } ?s <urn:p> ?local } AS ?value) { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "true,false")]
    [DataRow("SELECT (NOT EXISTS { BIND(STR(?s) AS ?local) FILTER(?local='urn:a') } AS ?value) { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "false,true")]
    [DataRow("SELECT (BOUND(?local) AS ?value) { VALUES ?s { <urn:a> <urn:b> } FILTER EXISTS { BIND(STR(?s) AS ?local) } } ORDER BY ?s", "false,false")]
    [DataRow("SELECT (EXISTS { GRAPH <urn:g> { ?s <urn:p> ?o } FILTER(?o='named') } AS ?value) { VALUES ?s { <urn:a> <urn:b> } } ORDER BY ?s", "false,true")]
    public void Sparql11_EXISTS_value_expressions_use_the_active_snapshot(string query, string expected)
    {
        var relation = new RdfQuad[]
        {
            new(new RdfIri("urn:a"), new("urn:p"), new RdfLiteral("default")),
            new(new RdfIri("urn:b"), new("urn:p"), new RdfLiteral("named"), new RdfIri("urn:g"))
        };
        var result = (RdfSparqlSelectResult)RdfSparql.ExecuteRelation(relation, query, default);
        Assert.AreEqual(expected, String.Join(',', result.Bindings.Select(row => row.GetValueOrDefault("value") is RdfLiteral literal
            ? literal.LexicalForm : "UNBOUND")));
    }
}
