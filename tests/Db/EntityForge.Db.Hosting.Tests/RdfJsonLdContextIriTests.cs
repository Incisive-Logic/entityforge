using System.Text;
using System.Text.Json;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfJsonLdContextIriTests
{
    [TestMethod]
    [DataRow("g:root/", "child", "g:root/child")]
    [DataRow("http://EXAMPLE.test:80/root/", "child", "http://EXAMPLE.test:80/root/child")]
    [DataRow("http://example.test/%2f/root/", "child", "http://example.test/%2f/root/child")]
    public void JsonLd_local_base_uses_generic_resolution_without_url_normalization(string basis, string reference, string expected)
    {
        string source = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = new Dictionary<string, string> { ["@base"] = basis },
            ["@id"] = reference,
            ["urn:p"] = new Dictionary<string, string> { ["@id"] = reference }
        });
        var quad = Parse(source).Single();
        Assert.AreEqual(new RdfIri(expected), quad.Subject);
        Assert.AreEqual(new RdfIri(expected), quad.Object);
    }

    [TestMethod]
    public void JsonLd_nested_relative_base_retains_its_parent_context()
    {
        var quads = Parse("""
            {"@context":{"@base":"g:root/","child":"urn:child"},"@id":"outer",
             "child":{"@context":{"@base":"nested/"},"@id":"inner","urn:p":{"@id":"item"}}}
            """);
        Assert.AreEqual(2, quads.Count);
        var link = quads.Single(q => q.Predicate.Value == "urn:child");
        Assert.AreEqual(new RdfIri("g:root/outer"), link.Subject);
        Assert.AreEqual(new RdfIri("g:root/nested/inner"), link.Object);
        var value = quads.Single(q => q.Predicate.Value == "urn:p");
        Assert.AreEqual(link.Object, value.Subject);
        Assert.AreEqual(new RdfIri("g:root/nested/item"), value.Object);
    }

    [TestMethod]
    public void JsonLd_null_base_removes_relative_identifier_resolution()
    {
        var quads = Parse("""
            {"@context":[{"@base":"g:root/"},{"@base":null}],"@id":"relative","urn:p":"value"}
            """);
        Assert.AreEqual(0, quads.Count);
    }

    [TestMethod]
    [DataRow("{\"ex\":\"g:\",\"value\":{\"@id\":\"ex:p\",\"@type\":\"ex:t\"}}")]
    [DataRow("[{\"value\":{\"@id\":\"urn:old\",\"@type\":\"urn:oldType\"}},{\"value\":{\"@id\":\"g:p\",\"@type\":\"g:t\"}}]")]
    public void JsonLd_context_prefixes_and_arrays_preserve_generic_datatypes(string context)
    {
        var quad = Parse("{\"@context\":" + context + ",\"@id\":\"g:s\",\"value\":\"g:h\"}").Single();
        AssertValue(quad, "g:s", "g:p", "g:t");
    }

    [TestMethod]
    public void JsonLd_property_scoped_context_does_not_rewrite_outer_literals()
    {
        var quads = Parse("""
            {"@context":{
              "value":{"@id":"urn:outer","@type":"urn:outerType"},
              "child":{"@id":"urn:child","@context":{"value":{"@id":"g:p","@type":"g:t"}}}},
             "@id":"urn:root","value":"g:h","child":{"@id":"g:s","value":"g:h"}}
            """);
        Assert.AreEqual(3, quads.Count);
        AssertValue(quads.Single(q => q.Predicate.Value == "g:p"), "g:s", "g:p", "g:t");
        AssertValue(quads.Single(q => q.Predicate.Value == "urn:outer"), "urn:root", "urn:outer", "urn:outerType");
    }

    [TestMethod]
    public void JsonLd_type_scoped_context_preserves_generic_datatypes()
    {
        var quads = Parse("""
            {"@context":{"T":{"@id":"g:Type","@context":{"value":{"@id":"g:p","@type":"g:t"}}}},
             "@id":"g:s","@type":"T","value":"g:h"}
            """);
        Assert.AreEqual(2, quads.Count);
        AssertValue(quads.Single(q => q.Predicate.Value == "g:p"), "g:s", "g:p", "g:t");
        Assert.IsTrue(quads.Any(q => q.Object == new RdfIri("g:Type")));
    }

    [TestMethod]
    public void JsonLd_json_literal_content_is_not_a_context_or_identifier()
    {
        var quad = Parse("""
            {"@context":{"data":{"@id":"g:p","@type":"@json"}},"@id":"g:s",
             "data":{"g:p":"g:h","@context":{"value":{"@type":"g:t"}}}}
            """).Single();
        var literal = (RdfLiteral)quad.Object;
        Assert.AreEqual("http://www.w3.org/1999/02/22-rdf-syntax-ns#JSON", literal.Datatype);
        using var json = JsonDocument.Parse(literal.LexicalForm);
        Assert.AreEqual("g:h", json.RootElement.GetProperty("g:p").GetString());
        Assert.AreEqual("g:t", json.RootElement.GetProperty("@context").GetProperty("value").GetProperty("@type").GetString());
    }

    [TestMethod]
    [DataRow("_:blank")]
    [DataRow("relative")]
    [DataRow("g:bad value")]
    public void JsonLd_context_rejects_invalid_datatype_iris(string datatype)
    {
        string context = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@context"] = new { value = new Dictionary<string, string> { ["@id"] = "urn:p", ["@type"] = datatype } }
        });
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(context));
    }

    [TestMethod]
    public void JsonLd_context_protected_term_cannot_be_redefined()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => Parse("""
            {"@context":[{"@protected":true,"value":{"@id":"g:p","@type":"g:t"}},
                         {"value":{"@id":"urn:different","@type":"urn:differentType"}}],
             "@id":"g:s","value":"g:h"}
            """));
    }

    [TestMethod]
    [DataRow("{\"@context\":\"g:context\",\"@id\":\"urn:s\"}")]
    [DataRow("{\"@context\":{\"@import\":\"g:context\"},\"@id\":\"urn:s\"}")]
    [DataRow("{\"@context\":{\"@base\":\"g:root/\"},\"@id\":\"bad value\",\"urn:p\":\"text\"}")]
    [DataRow("{\"@context\":[{\"@base\":\"g:root/\"},{\"@base\":\"bad value\"}],\"@id\":\"child\",\"urn:p\":\"text\"}")]
    public void JsonLd_invalid_or_disabled_iri_inputs_report_invalid_data(string source)
    {
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(source));
    }

    [TestMethod]
    public void JsonLd_reserved_identifiers_are_ignored_without_dropping_other_at_references()
    {
        var quads = Parse("""
            {"@context":{"@base":"https://example.test/"},"@id":"urn:subject",
             "urn:at":{"@id":"@"},"urn:punctuation":{"@id":"@future.term"},
             "urn:reserved":{"@id":"@futureTerm"}}
            """);
        Assert.AreEqual(2, quads.Count);
        Assert.IsTrue(quads.Any(q => q.Predicate == new RdfIri("urn:at") && q.Object == new RdfIri("https://example.test/@")));
        Assert.IsTrue(quads.Any(q => q.Predicate == new RdfIri("urn:punctuation") && q.Object == new RdfIri("https://example.test/@future.term")));
    }

    [TestMethod]
    [DataRow("\"@language\":\"en\"", "\"@language\":\"fr\"")]
    [DataRow("\"@context\":{\"value\":\"urn:first\"}", "\"@context\":{\"value\":\"urn:second\"}")]
    [DataRow("\"@context\":{\"value\":\"urn:first\"}", "\"@context\":null")]
    public void JsonLd_protected_definition_preserves_language_and_scoped_context(string first, string second)
    {
        string source = $$$"""
            {"@context":[{"term":{"@id":"urn:term","@protected":true,{{{first}}}}},
                         {"term":{"@id":"urn:term","@protected":true,{{{second}}}}}]}
            """;
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(source));
    }

    [TestMethod]
    public void JsonLd_protected_scoped_context_allows_equivalent_definition()
    {
        var quads = Parse("""
            {"@context":[{"term":{"@id":"urn:term","@protected":true,"@context":{"a":"urn:a","b":"urn:b"}}},
                         {"term":{"@id":"urn:term","@protected":true,"@context":{"b":"urn:b","a":"urn:a"}}}],
             "@id":"urn:s","term":{"@id":"urn:o","a":"value"}}
            """);
        Assert.AreEqual(2, quads.Count);
    }

    private static IReadOnlyList<RdfQuad> Parse(string source)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var result = RdfSyntax.ParseJsonLd(input, "context-test");
        Assert.IsTrue(input.CanRead);
        return result;
    }

    private static void AssertValue(RdfQuad quad, string subject, string predicate, string datatype)
    {
        Assert.AreEqual(new RdfIri(subject), quad.Subject);
        Assert.AreEqual(new RdfIri(predicate), quad.Predicate);
        var literal = (RdfLiteral)quad.Object;
        Assert.AreEqual(datatype, literal.Datatype);
        Assert.AreEqual("g:h", literal.LexicalForm);
    }
}
