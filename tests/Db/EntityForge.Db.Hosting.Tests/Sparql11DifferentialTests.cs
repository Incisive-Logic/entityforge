using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("SELECT (YEAR(?d) AS ?y) (MONTH(?d) AS ?m) (DAY(?d) AS ?day) { VALUES ?d {'2024-02-29T23:45:12Z'^^<http://www.w3.org/2001/XMLSchema#dateTime>} }")]
    [DataRow("SELECT (HOURS(?d) AS ?h) (MINUTES(?d) AS ?m) (SECONDS(?d) AS ?s) { VALUES ?d {'2024-02-29T23:45:12.34-05:30'^^<http://www.w3.org/2001/XMLSchema#dateTime>} }")]
    [DataRow("SELECT (TIMEZONE(?d) AS ?duration) { VALUES ?d {'2024-02-29T23:45:12-05:30'^^<http://www.w3.org/2001/XMLSchema#dateTime>} }")]
    [DataRow("SELECT (TZ(?d) AS ?z) { VALUES ?d {'2024-02-29T23:45:12'^^<http://www.w3.org/2001/XMLSchema#dateTime>} }")]
    [DataRow("SELECT (REPLACE('abracadabra', 'a(.)', 'a$1$1') AS ?x) {}")]
    [DataRow("SELECT (REPLACE('Hello', 'l', 'r') AS ?x) {}")]
    [DataRow("SELECT (REPLACE('ABab', 'a', 'x', 'i') AS ?x) {}")]
    [DataRow("SELECT ?x { VALUES ?s {'ab' 'ba'} BIND(REPLACE(?s,'a','x') AS ?x) FILTER(REPLACE(?s,'b','') = 'a') } ORDER BY ?x")]
    [DataRow("SELECT ?id { VALUES (?id ?a ?b) { ('x' 1 1) ('z' 2 3) ('y' 1 2) } } ORDER BY ?a DESC(?b)")]
    [DataRow("SELECT ?id { VALUES (?id ?a ?b) { ('x' 1 1) ('z' 2 3) ('y' 1 2) } } ORDER BY ?a DESC(?b) OFFSET 1 LIMIT 1")]
    [DataRow("SELECT ?id { VALUES (?id ?n) { ('z' 1) ('a' 1.0) } } ORDER BY ?n ?id")]
    [DataRow("SELECT * { VALUES ?v { <urn:a> 'Hello'@en } BIND(STR(?v) AS ?text) BIND(LANG(?v) AS ?lang) } ORDER BY ?text")]
    [DataRow("SELECT * { BIND(STR(?missing) AS ?text) }")]
    [DataRow("SELECT * { VALUES ?s { 'abc' 'AB' } BIND(UCASE(?s) AS ?upper) BIND(STRLEN(?s) AS ?length) } ORDER BY ?length")]
    [DataRow("SELECT ?id { { SELECT ?id ?a { VALUES (?id ?a) { ('x' 1) ('z' 2) ('y' 1) } } ORDER BY ?a DESC(?id) LIMIT 2 } } ORDER BY ?id")]
    [DataRow("SELECT (1+2*3 AS ?x) {}")]
    [DataRow("SELECT (7/2 AS ?x) {}")]
    [DataRow("SELECT (SUM(?n*2) AS ?x) { VALUES ?n {1 2 3} }")]
    [DataRow("SELECT (MIN(?n) AS ?x) (MAX(?n) AS ?y) { VALUES ?n {'b' 'a'} }")]
    [DataRow("SELECT (GROUP_CONCAT(?n; SEPARATOR=',') AS ?x) { VALUES ?n {'a' 'a'} }")]
    [DataRow("SELECT ?g (SUM(?n) AS ?x) { VALUES (?g ?n) { ('a' 1) ('a' 2) ('b' 1) } } GROUP BY ?g HAVING(COUNT(*) > 1) ORDER BY ?g")]
    [DataRow("SELECT (IF(true, 3, 1/0) AS ?x) {}")]
    [DataRow("SELECT (COALESCE(?missing, 1/0, 5) AS ?x) {}")]
    [DataRow("SELECT (CONCAT('a', STR(1+2)) AS ?x) {}")]
    [DataRow("SELECT (STRBEFORE('hello-world', '-') AS ?x) (STRAFTER('hello-world', '-') AS ?y) {}")]
    [DataRow("SELECT (SUBSTR('abcdef', 2, 2) AS ?x) {}")]
    [DataRow("SELECT (ABS(-1.25) AS ?a) (CEIL(-1.25) AS ?c) (FLOOR(-1.25) AS ?f) (ROUND(-1.6) AS ?r) {}")]
    [DataRow("SELECT (MD5('abc') AS ?a) (SHA1('abc') AS ?b) (SHA256('abc') AS ?c) (SHA384('abc') AS ?d) (SHA512('abc') AS ?e) {}")]
    [DataRow("SELECT (isURI(<urn:a>) AS ?u) (isNumeric(2) AS ?n) (STRENDS('hello'@en, 'lo') AS ?e) {}")]
    public async Task Sparql11_results_match_independent_evaluator(string query)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var actual = RdfSparql.Execute(database, query);
        using var store = new TripleStore();
        store.Add(new VDS.RDF.Graph());
        // TZ offsets, REPLACE language preservation, ROUND negative ties and ENCODE_FOR_URI have authored expected-result
        // coverage: this reference version disagrees with the specified results.
        // Compare against unoptimized algebra. VALUES/UNDEF with BIND errors has
        // explicit expected-bag coverage in Sparql11CompatibilityTests instead.
        var processor = new LeviathanQueryProcessor(store, options => options.AlgebraOptimisation = false);
        var parser = new SparqlQueryParser { QueryOptimisation = false };
        var expected = (SparqlResultSet)processor.ProcessQuery(parser.ParseFromString(query));
        string Native(RdfTerm term) => term switch
        {
            RdfIri iri => "I:" + iri.Value,
            RdfLiteral literal => "L:" + literal.LexicalForm + "^^" + literal.Datatype + "@" + literal.Language,
            _ => throw new InvalidDataException("Unexpected term in differential fixture.")
        };
        string Reference(INode term) => term switch
        {
            IUriNode iri => "I:" + iri.Uri.AbsoluteUri,
            ILiteralNode literal => "L:" + literal.Value + "^^" +
                (String.IsNullOrEmpty(literal.Language) ? literal.DataType?.AbsoluteUri ?? RdfLiteral.StringDatatype : RdfLiteral.LanguageStringDatatype) + "@" + literal.Language,
            _ => throw new InvalidDataException("Unexpected reference term in differential fixture.")
        };
        string[] observed = actual.Select(row => String.Join("|", row.OrderBy(pair => pair.Key)
            .Select(pair => pair.Key + "=" + Native(pair.Value)))).ToArray();
        string[] reference = expected.Select(row => String.Join("|", row.Where(pair => pair.Value is not null).OrderBy(pair => pair.Key)
            .Select(pair => pair.Key + "=" + Reference(pair.Value)))).ToArray();
        CollectionAssert.AreEqual(reference, observed, query + "\nExpected: " + String.Join("\n", reference) + "\nActual: " + String.Join("\n", observed));
    }
}
