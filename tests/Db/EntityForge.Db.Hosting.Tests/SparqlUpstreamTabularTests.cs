using System.Net;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using VDS.RDF;
using VDS.RDF.Parsing;
using VDS.RDF.Query;

namespace EntityForge.Db.Hosting.Tests;

public sealed partial class EntityForgeDbHostTests
{
    [TestMethod]
    [DataRow("csv01")]
    [DataRow("csv02")]
    [DataRow("csv03")]
    [DataRow("csv04")]
    [DataRow("tsv01")]
    [DataRow("tsv02")]
    [DataRow("tsv03")]
    public async Task Sparql11_external_W3C_tabular_results_through_http(string name)
    {
        string? configured = Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_TEST_ROOT");
        if (String.IsNullOrWhiteSpace(configured))
        {
            Assert.Inconclusive("Set ENTITYFORGE_W3C_TEST_ROOT to the pinned local W3C rdf-tests checkout.");
        }
        string root = Path.GetFullPath(configured).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string directory = Path.Combine(root, "sparql", "sparql11", "csv-tsv-res");
        using var manifest = new VDS.RDF.Graph();
        FileLoader.Load(manifest, Path.Combine(directory, "manifest.ttl"));
        const string mf = "http://www.w3.org/2001/sw/DataAccess/tests/test-manifest#";
        const string qt = "http://www.w3.org/2001/sw/DataAccess/tests/test-query#";
        INode Object(INode subject, string predicate) => manifest.GetTriplesWithSubjectPredicate(subject,
            manifest.CreateUriNode(new Uri(predicate))).Single().Object;
        string Local(INode node)
        {
            Assert.IsInstanceOfType<IUriNode>(node);
            var uri = ((IUriNode)node).Uri;
            Assert.IsTrue(uri.IsFile);
            string path = Path.GetFullPath(uri.LocalPath);
            Assert.IsTrue(path.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            return path;
        }
        var entry = manifest.CreateUriNode(new Uri("http://www.w3.org/2009/sparql/docs/tests/data-sparql11/csv-tsv-res/manifest#" + name));
        var action = Object(entry, mf + "action");
        string query = await File.ReadAllTextAsync(Local(Object(action, qt + "query")));
        string data = await File.ReadAllTextAsync(Local(Object(action, qt + "data")));
        string expectedText = await File.ReadAllTextAsync(Local(Object(entry, mf + "result")));
        _directory = NewDirectory();
        await using var host = EntityForgeDbServer.CreateBuilder().UseLocalStorage(_directory).UseRdfProfile()
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await host.StartAsync();
        using var client = Client(host);
        using var imported = await client.PostAsync($"/v1/rdf/import?documentId={Guid.NewGuid()}&pageNumber=0", new StringContent(data, Encoding.UTF8, "text/turtle"));
        Assert.AreEqual(HttpStatusCode.OK, imported.StatusCode, await imported.Content.ReadAsStringAsync());
        bool tsv = name.StartsWith("tsv", StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/sparql")
        {
            Content = new StringContent(query, Encoding.UTF8, "application/sparql-query")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(tsv ? "text/tab-separated-values" : "text/csv"));
        using var response = await client.SendAsync(request);
        string actualText = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, actualText);
        SparqlResultSet Parse(string text)
        {
            var result = new SparqlResultSet();
            if (tsv)
            {
                result.Dispose();
                return ParseTsvWithTurtle(text);
            }
            else
            {
                new SparqlCsvParser().Load(result, new StringReader(text));
            }
            return result;
        }
        using var expected = Parse(expectedText);
        using var actual = Parse(actualText);
        CollectionAssert.AreEqual(expected.Variables.ToArray(), actual.Variables.ToArray());
        Assert.AreEqual(expected.Count, actual.Count);
        var blanks = new Dictionary<INode, INode>();
        for (int row = 0; row < expected.Count; row++)
        {
            foreach (string column in expected.Variables)
            {
                bool bound = expected[row].HasBoundValue(column);
                Assert.AreEqual(bound, actual[row].HasBoundValue(column), $"{name} row {row}, {column}");
                if (!bound)
                {
                    continue;
                }
                var value = expected[row][column];
                var other = actual[row][column];
                if (value is IBlankNode)
                {
                    Assert.IsInstanceOfType<IBlankNode>(other);
                    if (blanks.TryGetValue(value, out var mapped))
                    {
                        Assert.AreEqual(mapped, other);
                    }
                    else
                    {
                        Assert.IsFalse(blanks.Values.Contains(other));
                        blanks.Add(value, other);
                    }
                }
                else
                {
                    // The upstream TSV fixture abbreviates a double with 'e'
                    // whereas its Turtle input uses 'E'. Compare the typed value;
                    // the protocol regression separately checks lexical retention.
                    if (tsv && value is ILiteralNode expectedNumber && other is ILiteralNode actualNumber &&
                        expectedNumber.DataType?.AbsoluteUri == "http://www.w3.org/2001/XMLSchema#double" &&
                        actualNumber.DataType == expectedNumber.DataType)
                    {
                        Assert.AreEqual(Double.Parse(expectedNumber.Value, CultureInfo.InvariantCulture),
                            Double.Parse(actualNumber.Value, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        Assert.AreEqual(value, other, $"{name} row {row}, {column}");
                    }
                }
            }
        }
        if (Environment.GetEnvironmentVariable("ENTITYFORGE_W3C_HTTP_REPORT") is { Length: > 0 } report)
        {
            File.WriteAllText(report + ".tabular-" + name + ".json",
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    cases = new[] { new { name = entry.ToString(), passed = true } }
                }));
        }
    }

    private static SparqlResultSet ParseTsvWithTurtle(string text)
    {
        // dotNetRDF's TSV tokeniser rejects valid uppercase-E DOUBLE tokens.
        // Decode cells with its independent Turtle parser, as required by TSV.
        string[] lines = text.Split('\n');
        Assert.AreEqual(String.Empty, lines[^1], "TSV output must end with LF.");
        string[] variables = lines[0].TrimEnd('\r').Split('\t').Select(name => name[1..]).ToArray();
        var turtle = new StringBuilder();
        for (int row = 1; row < lines.Length - 1; row++)
        {
            string[] cells = lines[row].TrimEnd('\r').Split('\t');
            Assert.AreEqual(variables.Length, cells.Length);
            for (int column = 0; column < cells.Length; column++)
            {
                if (cells[column].Length > 0)
                {
                    turtle.Append("<urn:tabular:row:").Append(row).Append("> <urn:tabular:column:").Append(column).Append("> ")
                        .Append(cells[column]).AppendLine(" .");
                }
            }
        }
        using var graph = new VDS.RDF.Graph();
        new TurtleParser().Load(graph, new StringReader(turtle.ToString()));
        var result = new SparqlResultSet();
        var handler = new VDS.RDF.Parsing.Handlers.ResultSetHandler(result);
        handler.StartResults();
        foreach (string variable in variables)
        {
            handler.HandleVariable(variable);
        }
        for (int row = 1; row < lines.Length - 1; row++)
        {
            handler.HandleResult(new SparqlResult(variables.Select((variable, column) => new KeyValuePair<string, INode>(variable,
                graph.GetTriplesWithSubjectPredicate(graph.CreateUriNode(new Uri("urn:tabular:row:" + row)),
                    graph.CreateUriNode(new Uri("urn:tabular:column:" + column))).SingleOrDefault()?.Object!))));
        }
        handler.EndResults(true);
        return result;
    }
}
