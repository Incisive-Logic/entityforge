using System.Net;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Microsoft.AspNetCore.Hosting;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public async Task Sparql_xml_responses_validate_against_explicitly_errata_adapted_W3C_schema(int shape)
    {
        string? schemaRoot = Environment.GetEnvironmentVariable("ENTITYFORGE_SPARQL_XML_SCHEMA_ROOT");
        if (String.IsNullOrWhiteSpace(schemaRoot))
        {
            Assert.Inconclusive("Set ENTITYFORGE_SPARQL_XML_SCHEMA_ROOT to inputs prepared by eng/Get-SparqlXmlSchemas.ps1.");
        }
        const string resultNamespace = "http://www.w3.org/2005/sparql-results#";
        string originalPath = Path.Combine(schemaRoot, "sparql-results-official.xsd");
        string xmlPath = Path.Combine(schemaRoot, "xml-namespace-official.xsd");
        Assert.AreEqual("EFD767C579F4068AE501BA195E08A5367465AD5F419DE32C6D34E80A90D9B3F8",
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(originalPath))));
        Assert.AreEqual("61960FB3131E38022CAAD5360E2F33A3382578AB3C80CD58BD74320EDE61B20C",
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(xmlPath))));

        // Approved editorial corrections: xml-schema-namespace-typo and
        // xml-schema-index-element at https://www.w3.org/2001/sw/DataAccess/XMLres-errata.
        // The source files stay unchanged; this is explicitly adapted validation.
        string original = await File.ReadAllTextAsync(originalPath);
        var schema = XDocument.Parse(original.Replace("http://www.w3.org/2007/SPARQL/results#", resultNamespace, StringComparison.Ordinal));
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        var indices = schema.Descendants(xs + "attribute").Where(node => (string?)node.Attribute("name") == "index").ToArray();
        var groups = schema.Descendants(xs + "attributeGroup").Where(node => (string?)node.Attribute("name") == "indexAttr").ToArray();
        Assert.AreEqual(2, indices.Length);
        Assert.AreEqual(1, groups.Length);
        foreach (var node in indices.Concat(groups))
        {
            node.Remove();
        }
        var schemas = new XmlSchemaSet { XmlResolver = null };
        var schemaSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using (var xmlReader = XmlReader.Create(xmlPath, schemaSettings))
        {
            schemas.Add(XNamespace.Xml.NamespaceName, xmlReader);
        }
        using (var schemaReader = schema.CreateReader())
        {
            schemas.Add(resultNamespace, schemaReader);
        }
        schemas.Compile();
        string query = shape switch
        {
            0 => """
                SELECT ?b ?again ?language ?typed ?missing {
                  BIND(BNODE('x') AS ?b) BIND(?b AS ?again)
                  BIND('line\r\n雪'@en AS ?language)
                  BIND('001'^^<http://www.w3.org/2001/XMLSchema#integer> AS ?typed)
                }
                """,
            1 => "ASK {}",
            2 => "ASK { FILTER(false) }",
            3 => "SELECT * {}",
            5 => "SELECT ?rank ?iri ?plain ?b ?same ?other ?missing { " +
                "BIND(<urn:雪> AS ?iri) BIND('' AS ?plain) BIND(BNODE() AS ?b) " +
                "BIND(?b AS ?same) BIND(BNODE() AS ?other) VALUES ?rank { 2 1 1 } } ORDER BY ?rank",
            _ => "SELECT * { FILTER(false) }"
        };
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sparql?query=" + Uri.EscapeDataString(query));
        request.Headers.Accept.ParseAdd("application/sparql-results+xml");
        using var response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        Assert.AreEqual("application/sparql-results+xml", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("utf-8", response.Content.Headers.ContentType?.CharSet);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            ValidationType = ValidationType.Schema,
            Schemas = schemas,
            ValidationFlags = XmlSchemaValidationFlags.ReportValidationWarnings,
            MaxCharactersInDocument = 65536
        };
        settings.ValidationEventHandler += (_, error) => throw error.Exception;
        using (var reader = XmlReader.Create(new StringReader(body), settings))
        {
            while (reader.Read())
            {
            }
        }
        XNamespace ns = resultNamespace;
        var root = XDocument.Parse(body).Root!;
        Assert.AreEqual(ns + "sparql", root.Name);
        // The XSD does not express every prose requirement; check these directly.
        CollectionAssert.AreEqual(new[] { ns + "head", ns + (shape is 1 or 2 ? "boolean" : "results") }, root.Elements().Select(node => node.Name).ToArray());
        if (shape is 1 or 2)
        {
            Assert.AreEqual(0, root.Element(ns + "head")!.Elements(ns + "variable").Count());
            Assert.AreEqual(shape == 1 ? "true" : "false", root.Element(ns + "boolean")!.Value);
        }
        else if (shape == 0)
        {
            CollectionAssert.AreEqual(new[] { "b", "again", "language", "typed", "missing" },
                root.Element(ns + "head")!.Elements(ns + "variable").Select(node => (string)node.Attribute("name")!).ToArray());
            var bindings = root.Element(ns + "results")!.Elements(ns + "result").Single().Elements(ns + "binding")
                .ToDictionary(node => (string)node.Attribute("name")!, node => node.Elements().Single());
            Assert.IsFalse(bindings.ContainsKey("missing"));
            Assert.AreEqual(ns + "bnode", bindings["b"].Name);
            Assert.AreEqual(ns + "bnode", bindings["again"].Name);
            Assert.AreEqual(bindings["b"].Value, bindings["again"].Value);
            Assert.AreEqual(ns + "literal", bindings["language"].Name);
            Assert.AreEqual("line\r\n雪", bindings["language"].Value);
            Assert.AreEqual("en", (string?)bindings["language"].Attribute(XNamespace.Xml + "lang"));
            Assert.IsNull(bindings["language"].Attribute("datatype"));
            Assert.AreEqual("001", bindings["typed"].Value);
            Assert.AreEqual("http://www.w3.org/2001/XMLSchema#integer", (string?)bindings["typed"].Attribute("datatype"));
        }
        else if (shape == 5)
        {
            CollectionAssert.AreEqual(new[] { "rank", "iri", "plain", "b", "same", "other", "missing" },
                root.Element(ns + "head")!.Elements(ns + "variable").Select(node => (string)node.Attribute("name")!).ToArray());
            var rows = root.Element(ns + "results")!.Elements(ns + "result").Select(row => row.Elements(ns + "binding")
                .ToDictionary(node => (string)node.Attribute("name")!, node => node.Elements().Single())).ToArray();
            CollectionAssert.AreEqual(new[] { "1", "1", "2" }, rows.Select(row => row["rank"].Value).ToArray());
            foreach (var row in rows)
            {
                Assert.AreEqual(6, row.Count);
                Assert.IsFalse(row.ContainsKey("missing"));
                Assert.AreEqual(ns + "uri", row["iri"].Name);
                Assert.AreEqual("urn:雪", row["iri"].Value);
                Assert.AreEqual(0, row["iri"].Attributes().Count());
                Assert.AreEqual(ns + "literal", row["plain"].Name);
                Assert.AreEqual("", row["plain"].Value);
                Assert.AreEqual("http://www.w3.org/2001/XMLSchema#string", (string?)row["plain"].Attribute("datatype"));
                foreach (string name in new[] { "b", "same", "other" })
                {
                    Assert.AreEqual(ns + "bnode", row[name].Name);
                    Assert.IsFalse(String.IsNullOrEmpty(row[name].Value));
                    Assert.AreEqual(0, row[name].Attributes().Count());
                }
                Assert.AreEqual(row["b"].Value, row["same"].Value);
                Assert.AreNotEqual(row["b"].Value, row["other"].Value);
                Assert.AreEqual(rows[0]["b"].Value, row["b"].Value);
                Assert.AreEqual(rows[0]["other"].Value, row["other"].Value);
            }
        }
        else
        {
            Assert.AreEqual(shape == 3 ? 1 : 0, root.Element(ns + "results")!.Elements(ns + "result").Count());
            Assert.AreEqual(0, root.Element(ns + "head")!.Elements(ns + "variable").Count());
        }
    }
}
