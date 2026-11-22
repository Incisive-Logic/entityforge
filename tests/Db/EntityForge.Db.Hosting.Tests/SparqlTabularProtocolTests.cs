using System.Net;
using System.Net.Http.Headers;
using System.Text;
using EntityForge.Db.Rdf;
using Microsoft.AspNetCore.Hosting;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    public async Task Sparql_tsv_abbreviates_numbers_without_changing_datatypes_or_lexical_forms()
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sparql")
        {
            Content = new StringContent("PREFIX xsd:<http://www.w3.org/2001/XMLSchema#> SELECT ?i ?d ?e ?whole ?inf ?subtype { VALUES (?i ?d ?e ?whole ?inf ?subtype) { ('+001'^^xsd:integer '02.20'^^xsd:decimal '1.0E6'^^xsd:double '2'^^xsd:decimal 'INF'^^xsd:double '-3'^^xsd:negativeInteger) } }", Encoding.UTF8, "application/sparql-query")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/tab-separated-values"));
        using var response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, text);
        Assert.IsTrue(text.Contains("\n+001\t02.20\t1.0E6\t", StringComparison.Ordinal), text);
        using var parsed = ParseTsvWithTurtle(text);
        foreach (var (column, lexical, datatype) in new[]
        {
            ("i", "+001", "integer"), ("d", "02.20", "decimal"), ("e", "1.0E6", "double"),
            ("whole", "2", "decimal"), ("inf", "INF", "double"), ("subtype", "-3", "negativeInteger")
        })
        {
            var literal = (ILiteralNode)parsed[0][column];
            Assert.AreEqual(lexical, literal.Value);
            Assert.AreEqual("http://www.w3.org/2001/XMLSchema#" + datatype, literal.DataType.AbsoluteUri);
        }
    }

    [TestMethod]
    [DataRow("GET", false)]
    [DataRow("FORM", false)]
    [DataRow("RAW", false)]
    [DataRow("GET", true)]
    [DataRow("FORM", true)]
    [DataRow("RAW", true)]
    public async Task Sparql11_tabular_protocol_preserves_escaping_headers_and_blank_identity(string transport, bool tsv)
    {
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .UseBearerTokenSecurity(new(AdministratorToken, readerToken: ReaderToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host, ReaderToken);
        string media = tsv ? "text/tab-separated-values" : "text/csv";
        foreach (string query in new[]
        {
            """
            SELECT ?iri ?text ?number ?empty ?missing ?blank ?same {
              BIND(<urn:a> AS ?iri) BIND('a,"line\n\t雪'@en AS ?text) BIND(42 AS ?number)
              BIND('' AS ?empty) BIND(BNODE('x') AS ?blank) BIND(?blank AS ?same)
            }
            """,
            "SELECT ?missing WHERE { ?missing <urn:absent> ?o }"
        })
        {
            using var request = new HttpRequestMessage(transport == "GET" ? HttpMethod.Get : HttpMethod.Post,
                transport == "GET" ? "/sparql?query=" + Uri.EscapeDataString(query) : "/sparql");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(media));
            if (transport == "FORM")
            {
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["query"] = query });
            }
            else if (transport == "RAW")
            {
                request.Content = new StringContent(query, Encoding.UTF8, "application/sparql-query");
            }
            using var response = await client.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
            Assert.AreEqual(media, response.Content.Headers.ContentType?.MediaType);
            Assert.AreEqual("utf-8", response.Content.Headers.ContentType?.CharSet);
            Assert.IsTrue(body.StartsWith(tsv ? "?iri\t" : "iri,", StringComparison.Ordinal) ||
                body == (tsv ? "?missing\n" : "missing\r\n"), body);
            var parsed = new SparqlResultSet();
            if (tsv)
            {
                new SparqlTsvParser().Load(parsed, new StringReader(body));
            }
            else
            {
                new SparqlCsvParser().Load(parsed, new StringReader(body));
            }
            if (query.Contains("urn:absent", StringComparison.Ordinal))
            {
                Assert.AreEqual(0, parsed.Count);
                CollectionAssert.AreEqual(new[] { "missing" }, parsed.Variables.ToArray());
                continue;
            }
            Assert.AreEqual(1, parsed.Count);
            Assert.AreEqual(7, parsed.Variables.Count());
            Assert.AreEqual("a,\"line\n\t雪", ((ILiteralNode)parsed[0]["text"]).Value);
            Assert.AreEqual("42", ((ILiteralNode)parsed[0]["number"]).Value);
            Assert.AreEqual(parsed[0]["blank"], parsed[0]["same"]);
            Assert.IsFalse(parsed[0].HasBoundValue("missing"));
            if (tsv)
            {
                Assert.AreEqual("en", ((ILiteralNode)parsed[0]["text"]).Language);
                Assert.AreEqual(String.Empty, ((ILiteralNode)parsed[0]["empty"]).Value);
                Assert.AreEqual(new Uri("http://www.w3.org/2001/XMLSchema#integer"), ((ILiteralNode)parsed[0]["number"]).DataType);
            }
        }
    }
}
