using EntityForge.Db.Recovery.Worker;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class QueryWorkloadDatasetOracleTests
{
    [TestMethod]
    [DataRow(1, false, false)]
    [DataRow(3, false, true)]
    [DataRow(7, true, false)]
    [DataRow(11, true, true)]
    public void Complete_dataset_accepts_reordered_values_and_wraparound(int size, bool aggregate, bool hubs)
    {
        int fanOut = Math.Min(size, 5);
        var oracle = new QueryWorkloadDatasetOracle(size, fanOut, aggregate, hubs, 2);
        foreach (var quad in Dataset(size, fanOut, aggregate, hubs, 2).Reverse())
        {
            oracle.Observe(quad);
        }
        oracle.Complete();
    }

    [TestMethod]
    [DataRow("value")]
    [DataRow("datatype")]
    [DataRow("language")]
    [DataRow("graph")]
    [DataRow("subject")]
    [DataRow("predicate")]
    [DataRow("route")]
    [DataRow("lexical")]
    [DataRow("remainder")]
    public void Cardinality_preserving_corruption_is_rejected(string corruption)
    {
        RdfQuad bad = corruption switch
        {
            "value" => new(new RdfIri("urn:benchmark:0"), new("urn:key"), new RdfLiteral("1")),
            "datatype" => new(new RdfIri("urn:benchmark:0"), new("urn:key"), new RdfLiteral("0", "http://www.w3.org/2001/XMLSchema#integer")),
            "language" => new(new RdfIri("urn:benchmark:0"), new("urn:key"), new RdfLiteral("0", language: "en")),
            "graph" => new(new RdfIri("urn:benchmark:0"), new("urn:key"), new RdfLiteral("0"), new RdfIri("urn:other")),
            "subject" => new(new RdfIri("urn:benchmark:99"), new("urn:key"), new RdfLiteral("99")),
            "predicate" => new(new RdfIri("urn:benchmark:0"), new("urn:other"), new RdfLiteral("0")),
            "route" => new(new RdfIri("urn:benchmark:0"), new("urn:route"), new RdfIri("urn:benchmark:0")),
            "lexical" => new(new RdfIri("urn:benchmark:00"), new("urn:key"), new RdfLiteral("0")),
            _ => new(new RdfIri("urn:benchmark:remainder:1"), new("urn:remainder"), new RdfLiteral("1"))
        };
        var oracle = new QueryWorkloadDatasetOracle(7, 5, false, false, 1);
        Assert.ThrowsExactly<InvalidDataException>(() => oracle.Observe(bad));
    }

    [TestMethod]
    public void Repeated_statement_cannot_replace_missing_statement()
    {
        var oracle = new QueryWorkloadDatasetOracle(2, 0, false, false, 0);
        var quad = Dataset(2, 0, false, false, 0).First();
        oracle.Observe(quad);
        Assert.ThrowsExactly<InvalidDataException>(() => oracle.Observe(quad));
        Assert.ThrowsExactly<InvalidDataException>(() => oracle.Complete());
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("property")]
    [DataRow("label")]
    [DataRow("endpoint")]
    [DataRow("edge-label")]
    [DataRow("edge-properties")]
    public void Graph_verification_checks_values_and_endpoints(string variation)
    {
        var one = new GraphVertexId(new Guid("00000001-0000-0000-0000-000000000000"));
        var two = new GraphVertexId(new Guid("00000002-0000-0000-0000-000000000000"));
        var firstEdge = new GraphEdgeId(new Guid("00000001-0001-0000-0000-000000000000"));
        var secondEdge = new GraphEdgeId(new Guid("00000002-0001-0000-0000-000000000000"));
        var vertices = new Dictionary<GraphVertexId, GraphVertex>
        {
            [one] = new(one, new HashSet<string> { variation == "label" ? "Other" : "Benchmark" },
                new Dictionary<string, DatabaseValue> { ["key"] = new DatabaseValue.Int64(variation == "property" ? 1 : 0) }),
            [two] = new(two, new HashSet<string> { "Benchmark" },
                new Dictionary<string, DatabaseValue> { ["key"] = new DatabaseValue.Int64(1) })
        };
        var properties = new Dictionary<string, DatabaseValue>();
        if (variation == "edge-properties")
        {
            properties["extra"] = new DatabaseValue.Int64(0);
        }
        var edges = new Dictionary<GraphEdgeId, GraphEdge>
        {
            [firstEdge] = new(firstEdge, one, variation == "endpoint" ? one : two, variation == "edge-label" ? "other" : "route", properties),
            [secondEdge] = new(secondEdge, two, one, "route", new Dictionary<string, DatabaseValue>())
        };
        var oracle = new QueryWorkloadDatasetOracle(2, 1, false, false, 0);
        if (variation == "valid")
        {
            oracle.VerifyGraph(vertices, edges, CancellationToken.None);
        }
        else
        {
            Assert.ThrowsExactly<InvalidDataException>(() => oracle.VerifyGraph(vertices, edges, CancellationToken.None));
        }
    }

    private static IEnumerable<RdfQuad> Dataset(int size, int fanOut, bool aggregate, bool hubs, int remainder)
    {
        for (int key = 0; key < size; key++)
        {
            var subject = new RdfIri($"urn:benchmark:{key}");
            yield return new(subject, new("urn:key"), new RdfLiteral(key.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (aggregate)
            {
                yield return new(subject, new("urn:bucket"), new RdfLiteral((key % 10).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            for (int hop = 1; hop <= fanOut; hop++)
            {
                yield return new(subject, new("urn:route"), new RdfIri($"urn:benchmark:{(hubs ? hop - 1 : (key + hop) % size)}"));
            }
        }
        for (int key = 0; key < remainder; key++)
        {
            yield return new(new RdfIri($"urn:benchmark:remainder:{key}"), new("urn:remainder"), new RdfLiteral(key.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
