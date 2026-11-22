using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class RdfDatabaseEngineTests
{
    [TestMethod]
    [DataRow("123")]
    [DataRow("-en")]
    [DataRow("en-")]
    [DataRow("en--GB")]
    [DataRow("en_US")]
    [DataRow("日本語")]
    public void Rdf_literal_contract_rejects_invalid_language_tag_syntax(string language)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RdfLiteral("value", language: language));
    }

    [TestMethod]
    [DataRow("EN-gb")]
    [DataRow("es-419")]
    [DataRow("zh-Hant-CN")]
    [DataRow("x-private")]
    [DataRow("i-klingon")]
    public async Task Sparql11_language_tag_literals_and_constructors_agree(string language)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database,
            $"SELECT ('value'@{language} AS ?literal) (STRLANG('value','{language}') AS ?constructed) {{}}").Single();
        Assert.AreEqual(new RdfLiteral("value", language: language), row["literal"]);
        Assert.AreEqual(row["literal"], row["constructed"]);
    }

    [TestMethod]
    [DataRow("123")]
    [DataRow("-en")]
    [DataRow("en-")]
    [DataRow("en--GB")]
    public async Task Sparql11_literal_language_tags_follow_the_standard_grammar(string language)
    {
        await using var database = await OpenAsync(CreateDirectory());
        foreach (string literal in new[] { "'value'", "\"value\"", "'''value'''", "\"\"\"value\"\"\"" })
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                RdfSparql.Execute(database, $"SELECT ?x {{ VALUES ?x {{ {literal}@{language} }} }}"));
        }
    }

    [TestMethod]
    [DataRow("en\\n")]
    [DataRow("en\\r")]
    [DataRow("en\\t")]
    [DataRow(" en")]
    [DataRow("en_US")]
    [DataRow("en-")]
    public async Task Sparql11_strlang_invalid_tags_are_expression_errors(string language)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database,
            $"SELECT (STRLANG('value','{language}') AS ?bad) (COALESCE(STRLANG('value','{language}'),'fallback') AS ?recovered) {{}}").Single();
        Assert.IsFalse(row.ContainsKey("bad"));
        Assert.AreEqual(new RdfLiteral("fallback"), row["recovered"]);
        Assert.AreEqual(0, RdfSparql.Execute(database, $"SELECT * {{ FILTER(STRLANG('value','{language}')) }}").Count);
    }

    [TestMethod]
    [DataRow("REPLACE('abracadabra','bra','*')", "a*cada*")]
    [DataRow("REPLACE('abracadabra','a(.)','a$1$1')", "abbraccaddabbra")]
    [DataRow("REPLACE('ab','(a)','$12-$9-$0')", "a2--ab")]
    [DataRow("REPLACE('AAAA','a+','b','i')", "b")]
    [DataRow("REPLACE('abc','z','x')", "abc")]
    [DataRow("REPLACE('aaa','aa','x')", "xa")]
    [DataRow("REPLACE('abc','b','')", "ac")]
    [DataRow("REPLACE('abc','b','\\\\$')", "a$c")]
    [DataRow("REPLACE('abc','b','\\\\\\\\')", "a\\c")]
    [DataRow("REPLACE('abc','(a)(b)','$23')", "b3c")]
    [DataRow("REPLACE('abc','(a)(b)','$0002')", "bc")]
    [DataRow("REPLACE('abc','(a)(b)','$00099')", "9c")]
    [DataRow("REPLACE('abc','(a)(b)','$0000')", "abc")]
    [DataRow("REPLACE('a\\nb','^b','x','m')", "a\nx")]
    [DataRow("REPLACE('a\\nb','a.b','x','s')", "x")]
    [DataRow("REPLACE('ab','a b','x','x')", "x")]
    [DataRow("REPLACE('a😀b','😀','雪')", "a雪b")]
    public async Task Sparql11_replace_preserves_language_tags(string expression, string expected)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"SELECT (STRLANG({expression}, 'en') AS ?x) {{}}").Single();
        Assert.AreEqual(new RdfLiteral(expected, language: "en"), row["x"]);
        row = RdfSparql.Execute(database, "SELECT (REPLACE('Hello'@en,'l','r') AS ?x) {}").Single();
        Assert.AreEqual(new RdfLiteral("Herro", language: "en"), row["x"]);
    }

    [TestMethod]
    public async Task Sparql11_replace_enforces_output_limit_before_allocating_result()
    {
        await using var database = await OpenAsync(CreateDirectory());
        string query = $"SELECT (REPLACE('{new string('a', 2000)}','a','{new string('b', 1000)}') AS ?x) {{}}";
        Assert.ThrowsExactly<RdfSparqlWorkLimitException>(() => RdfSparql.Execute(database, query));
    }

    [TestMethod]
    public async Task Sparql11_function_names_can_also_be_prefix_names()
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database,
            "PREFIX ABS: <urn:example:> SELECT ?x { VALUES ?x { ABS:item } }").Single();
        Assert.AreEqual(new RdfIri("urn:example:item"), row["x"]);
    }

    [TestMethod]
    public async Task Sparql11_generated_values_are_fresh_per_solution_and_cached_execution()
    {
        await using var database = await OpenAsync(CreateDirectory());
        const string query = "SELECT (UUID() AS ?u) (STRUUID() AS ?s) (RAND() AS ?r) { VALUES ?n {1 2 3} }";
        var rows = RdfSparql.Execute(database, query).Concat(RdfSparql.Execute(database, query)).ToArray();
        var identifiers = new HashSet<Guid>();
        foreach (var row in rows)
        {
            var iri = (RdfIri)row["u"];
            Assert.IsTrue(iri.Value.StartsWith("urn:uuid:", StringComparison.Ordinal));
            Assert.IsTrue(Guid.TryParseExact(iri.Value[9..], "D", out var iriId));
            Assert.AreNotEqual(Guid.Empty, iriId);
            Assert.IsTrue(identifiers.Add(iriId));
            var text = (RdfLiteral)row["s"];
            Assert.AreEqual(RdfLiteral.StringDatatype, text.Datatype);
            Assert.IsTrue(Guid.TryParseExact(text.LexicalForm, "D", out var textId));
            Assert.AreNotEqual(Guid.Empty, textId);
            Assert.IsTrue(identifiers.Add(textId));
            var number = (RdfLiteral)row["r"];
            Assert.AreEqual("http://www.w3.org/2001/XMLSchema#double", number.Datatype);
            double value = Double.Parse(number.LexicalForm, System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsTrue(value >= 0 && value < 1);
        }
        Assert.AreEqual(6, rows.Length);
    }

    [TestMethod]
    [DataRow("ABS(-123)", "123", "integer")]
    [DataRow("ABS(-1.25)", "1.25", "decimal")]
    [DataRow("CEIL(-1.25)", "-1", "decimal")]
    [DataRow("CEIL(9007199254740993)", "9007199254740993", "integer")]
    [DataRow("CEIL(9007199254740993.1)", "9007199254740994", "decimal")]
    [DataRow("FLOOR(9007199254740993.9)", "9007199254740993", "decimal")]
    [DataRow("CEIL(-9007199254740993.9)", "-9007199254740993", "decimal")]
    [DataRow("FLOOR(-9007199254740993.1)", "-9007199254740994", "decimal")]
    [DataRow("CEIL(0.0000000000000000000000000001)", "1", "decimal")]
    [DataRow("FLOOR(-0.0000000000000000000000000001)", "-1", "decimal")]
    [DataRow("CEIL(9223372036854775807)", "9223372036854775807", "integer")]
    [DataRow("FLOOR(-9223372036854775808)", "-9223372036854775808", "integer")]
    [DataRow("FLOOR(-1.25)", "-2", "decimal")]
    [DataRow("ROUND(-1.5)", "-1", "decimal")]
    [DataRow("ROUND(1.5)", "2", "decimal")]
    [DataRow("ROUND(-1.6)", "-2", "decimal")]
    [DataRow("ROUND(1.49)", "1", "decimal")]
    [DataRow("ROUND(999999999999999999999999999999.5)", "1000000000000000000000000000000", "decimal")]
    [DataRow("ROUND(4503599627370497e0)", "4503599627370497", "double")]
    [DataRow("ROUND(-0.5e0)", "-0", "double")]
    [DataRow("ABS('-INF'^^<http://www.w3.org/2001/XMLSchema#double>)", "INF", "double")]
    [DataRow("ROUND('NaN'^^<http://www.w3.org/2001/XMLSchema#float>)", "NaN", "float")]
    [DataRow("CEIL('1.2'^^<http://www.w3.org/2001/XMLSchema#float>)", "2", "float")]
    [DataRow("ENCODE_FOR_URI('a b/é😀')", "a%20b%2F%C3%A9%F0%9F%98%80", "string")]
    [DataRow("ENCODE_FOR_URI('a-._~')", "a-._~", "string")]
    [DataRow("MD5('abc')", "900150983cd24fb0d6963f7d28e17f72", "string")]
    [DataRow("SHA1('abc')", "a9993e364706816aba3e25717850c26c9cd0d89d", "string")]
    [DataRow("SHA256('abc')", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "string")]
    [DataRow("SHA384('abc')", "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7", "string")]
    [DataRow("SHA512('abc')", "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f", "string")]
    [DataRow("STRENDS('hello'@en, 'lo')", "true", "boolean")]
    [DataRow("isURI(<urn:a>)", "true", "boolean")]
    [DataRow("isNumeric(1)", "true", "boolean")]
    [DataRow("isNumeric('NaN'^^<http://www.w3.org/2001/XMLSchema#double>)", "true", "boolean")]
    [DataRow("isNumeric('Infinity'^^<http://www.w3.org/2001/XMLSchema#double>)", "false", "boolean")]
    [DataRow("isNumeric('nan'^^<http://www.w3.org/2001/XMLSchema#double>)", "false", "boolean")]
    [DataRow("isNumeric('1')", "false", "boolean")]
    [DataRow("isNumeric('1.2'^^<http://www.w3.org/2001/XMLSchema#integer>)", "false", "boolean")]
    [DataRow("isNumeric('bad'^^<http://www.w3.org/2001/XMLSchema#integer>)", "false", "boolean")]
    public async Task Sparql11_builtins_preserve_values_and_datatypes(string expression, string expected, string datatype)
    {
        await using var database = await OpenAsync(CreateDirectory());
        var row = RdfSparql.Execute(database, $"SELECT ({expression} AS ?x) {{}}").Single();
        Assert.AreEqual(new RdfLiteral(expected, "http://www.w3.org/2001/XMLSchema#" + datatype), row["x"]);
    }

    [TestMethod]
    [DataRow("ABS('1')")]
    [DataRow("REGEX('a','\\\\p{IsBadBlockName}')")]
    [DataRow("REGEX('a','[\\\\P{IsBadBlockName}]')")]
    [DataRow("REPLACE('a','\\\\p{IsBadBlockName}','x')")]
    [DataRow("REGEX('a','(a\\\\1)')")]
    [DataRow("REGEX('a','\\\\1(a)')")]
    [DataRow("REGEX('a','(a)\\\\0')")]
    [DataRow("REGEX('a','[\\\\1]')")]
    [DataRow("REGEX('a','(?=a)')")]
    [DataRow("REGEX('a','(?<name>a)')")]
    [DataRow("REGEX('a','\\\\b')")]
    [DataRow("REGEX('aa','(a)\\\\k<1>')")]
    [DataRow("REPLACE('b','(a)?\\\\1','x')")]
    [DataRow("REPLACE('abc','','x')")]
    [DataRow("REPLACE('abc','a*','x')")]
    [DataRow("REPLACE('abc','[','x')")]
    [DataRow("REPLACE('abc','a','$')")]
    [DataRow("REPLACE('abc','z','$x')")]
    [DataRow("REPLACE('abc','a','x','z')")]
    [DataRow("REPLACE('abc','','x','q')")]
    [DataRow("REPLACE('abc','a'@en,'x')")]
    [DataRow("REPLACE('abc','a',1)")]
    [DataRow("REPLACE(1,'a','x')")]
    [DataRow("REPLACE(?missing,'a','x')")]
    [DataRow("FLOOR(<urn:a>)")]
    [DataRow("SHA256('abc'@en)")]
    [DataRow("ENCODE_FOR_URI(1)")]
    [DataRow("STRENDS('hello'@en, 'lo'@fr)")]
    [DataRow("isNumeric(?missing)")]
    public async Task Sparql11_builtin_errors_leave_alias_unbound(string expression)
    {
        await using var database = await OpenAsync(CreateDirectory());
        Assert.AreEqual(0, RdfSparql.Execute(database, $"SELECT ({expression} AS ?x) {{}}").Single().Count);
    }
}
