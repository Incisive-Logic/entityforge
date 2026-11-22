using System.Text.Json;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using Google.Protobuf;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using Wire = EntityForge.Db.Distributed.Hosting.QueryWire;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Typed_operator_roundtrip_preserves_graph_values_and_exact_logical_budget()
    {
        Guid id = Guid.NewGuid(), other = Guid.NewGuid(), edge = Guid.NewGuid();
        DatabaseValue[] values = [new DatabaseValue.String("quoted \" ÃƒÂ© Ã¦â€”Â¥Ã¦Å“Â¬\n"),
            new DatabaseValue.Int64(long.MinValue),
            new DatabaseValue.Double(double.NaN),
            new DatabaseValue.Boolean(false),
            new DatabaseValue.DateTimeOffset(DateTimeOffset.Parse("2026-10-02T12:34:56+05:30")),
            new DatabaseValue.Date(new(-50000, 2, 29)),
            new DatabaseValue.Duration(new(14, -2, -1, 500_000_000)),
            new DatabaseValue.Duration(new(0, 0, 0, 0), DatabaseDurationQualifier.YearToMonth),
            new DatabaseValue.Duration(new(0, 0, 0, 0), DatabaseDurationQualifier.DayToSecond),
            new DatabaseValue.Duration(new(14, 0, 0, 0), DatabaseDurationQualifier.YearToMonth),
            new DatabaseValue.Array([new DatabaseValue.Duration(new(0, 0, 0, 0), DatabaseDurationQualifier.YearToMonth),
                new DatabaseValue.Duration(new(0, 0, 0, 0), DatabaseDurationQualifier.DayToSecond)]),
            new DatabaseValue.Point(new(7203, 1.25, -2)),
            new DatabaseValue.Point(new(4979, 179, 52, 123)),
            new DatabaseValue.LocalTime(new(86_399_999_999_999)),
            new DatabaseValue.ZonedTime(new(new(86_399_999_999_999), -3723)),
            new DatabaseValue.ZonedDateTime(new(new(new(-10000, 1, 1), new(1)), 561, "Europe/Paris")),
            new DatabaseValue.Array([new DatabaseValue.ZonedDateTime(new(new(new(2020, 2, 29), new(86_399_999_999_999)), -64800))]),
            new DatabaseValue.Array([new DatabaseValue.ZonedTime(new(new(0), 64800)), new DatabaseValue.ZonedTime(new(new(1), -64800))]),
            new DatabaseValue.LocalDateTime(new(new(-10000, 1, 1), new(1))),
            new DatabaseValue.Array([new DatabaseValue.LocalDateTime(new(new(2020, 2, 29), new(86_399_999_999_999)))]),
            new DatabaseValue.Array([new DatabaseValue.LocalTime(new(0)), new DatabaseValue.LocalTime(new(1))]),
            new DatabaseValue.Array([new DatabaseValue.Point(new(4326, -179, 0)), new DatabaseValue.Point(new(9157, 1, 2, 3))]),
            new DatabaseValue.Array([new DatabaseValue.Duration(new(0, 0, -1, 500_000_000))]),
            new DatabaseValue.Array([new DatabaseValue.Date(new(2020, 2, 29))]),
            new DatabaseValue.Uri(new Uri("urn:typed:test")),
            new DatabaseValue.Binary(new byte[] { 0, 255 }),
            new DatabaseValue.Json("{\"n\":[null,true,3]}"),
            new DatabaseValue.Array([new DatabaseValue.Int64(1), new DatabaseValue.Int64(2)]),
            new DatabaseValue.Array([new DatabaseValue.Double(Double.NaN), new DatabaseValue.Double(Double.PositiveInfinity)]),
            new DatabaseValue.Array([]),
            new DatabaseValue.Decimal(0.1234567890123456789012345678m),
            new DatabaseValue.Array([new DatabaseValue.Decimal(-123.4500m), new DatabaseValue.Decimal(Decimal.MaxValue)]),
            new DatabaseValue.Uuid(Guid.Empty),
            new DatabaseValue.Uuid(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")),
            new DatabaseValue.Array([new DatabaseValue.Uuid(Guid.Empty), new DatabaseValue.Uuid(id)])];
        var properties = values.Select((v, i) => new KeyValuePair<string, DatabaseValue>("p" + i, v)).ToDictionary();
        var relation = new PartitionQueryRelation(
            Vertices: [new(new(id), new HashSet<string> { "person", "ÃƒÂ©lÃƒÂ¨ve" }, properties)],
            Edges: [new(new(edge), new(id), new(other), "peer", properties)],
            Adjacency: [new(new(edge), new(id), new(other), "peer")], GraphBindings: [id, id],
            PropertyBags: [new(false, id, properties)]);
        var page = new PartitionQueryExchangePage(relation, 7);
        var control = OperatorControl();
        var (observed, result) = PartitionQueryOperatorWire.Decode(PartitionQueryOperatorWire.Encode(control, new(true, new(page), false)));
        Assert.AreEqual(control, observed);
        var decoded = (PartitionQueryExchangePage)result.Value!.Native!;
        Assert.AreEqual(page.NextOffset, decoded.NextOffset);
        Assert.AreEqual(PartitionProjectionValue.Measure(page, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options),
            PartitionProjectionValue.Measure(decoded, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options));
        CollectionAssert.AreEqual(relation.GraphBindings, decoded.Relation.GraphBindings);
        foreach (var pair in properties)
        {
            Assert.AreEqual(pair.Value, decoded.Relation.Vertices![0].Properties[pair.Key]);
        }

        Assert.IsTrue(double.IsNaN(((DatabaseValue.Double)decoded.Relation.Edges![0].Properties["p2"]).Value));
    }

    [TestMethod]
    public void Typed_operator_preserves_empty_selected_arrays_and_rdf_quoted_terms()
    {
        var bindings = new RdfBindingFragment(0, new()
        {
            ["s"] = new RdfIri("urn:s"),
            ["o"] = new RdfTripleTerm(new RdfIri("urn:a"), new RdfIri("urn:p"), new RdfLiteral("value", language: "fr"))
        });
        var page = new PartitionQueryExchangePage(new(RdfBindings: [bindings]), null);
        var (_, result) = PartitionQueryOperatorWire.Decode(PartitionQueryOperatorWire.Encode(OperatorControl(), new(true, new(page), false)));
        var data = new PartitionQueryData();
        data.AddNativeProfilePage(((PartitionQueryExchangePage)result.Value!.Native!).Relation);
        Assert.IsTrue(data.HasRows("rdfBindings"));
        Assert.AreEqual(JsonSerializer.Serialize(bindings, PartitionQueryJson.Options), JsonSerializer.Serialize(data.TypedRows<RdfBindingFragment>("rdfBindings").Single(), PartitionQueryJson.Options));
        page = new(new(Vertices: [], GraphBindings: []), null);
        (_, result) = PartitionQueryOperatorWire.Decode(PartitionQueryOperatorWire.Encode(OperatorControl(), new(true, new(page), false)));
        Assert.IsNotNull(((PartitionQueryExchangePage)result.Value!.Native!).Relation.Vertices);
        Assert.AreEqual(0, ((PartitionQueryExchangePage)result.Value.Native!).Relation.Vertices!.Length);
    }

    [TestMethod]
    public void Typed_operator_rejects_wrong_format_duplicate_properties_and_fact_overflow()
    {
        var valid = PartitionQueryOperatorWire.Encode(OperatorControl(), new(true, new(new PartitionQueryExchangePage(new(Vertices: []), null)), false));
        var reply = Wire.Reply.Parser.ParseFrom(valid);
        reply.Format++;
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionQueryOperatorWire.Decode(reply.ToByteArray()));
        reply.Format--;
        var vertex = new Wire.Vertex { Id = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()) };
        vertex.Properties.Add(new Wire.Property { Name = "p", Value = new Wire.Value { Text = "one" } });
        vertex.Properties.Add(new Wire.Property { Name = "p", Value = new Wire.Value { Text = "two" } });
        reply.Page.Vertices.Add(vertex);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionQueryOperatorWire.Decode(reply.ToByteArray()));
        reply.Page.Vertices.Clear();
        for (int i = 0; i <= PartitionQueryLimits.ExchangePageFacts; i++)
        {
            reply.Page.Vertices.Add(vertex);
        }

        Assert.ThrowsExactly<InvalidDataException>(() => PartitionQueryOperatorWire.Decode(reply.ToByteArray()));
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionQueryOperatorWire.Decode(new byte[PartitionParticipantWire.MaximumResponseBytes + 1]));
    }

    [TestMethod]
    [DataRow(3u)]
    [DataRow(257u)]
    [DataRow(uint.MaxValue)]
    public void Typed_operator_rejects_unknown_duration_qualifiers(uint qualifier)
    {
        var valid = PartitionQueryOperatorWire.Encode(OperatorControl(), new(true, new(new PartitionQueryExchangePage(new(Vertices: []), null)), false));
        var reply = Wire.Reply.Parser.ParseFrom(valid);
        var vertex = new Wire.Vertex { Id = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()) };
        vertex.Properties.Add(new Wire.Property { Name = "span", Value = new Wire.Value { Duration = new Wire.CalendarDuration { Qualifier = qualifier } } });
        reply.Page.Vertices.Add(vertex);
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionQueryOperatorWire.Decode(reply.ToByteArray()));
    }

    [TestMethod]
    public void Typed_operator_does_not_admit_small_binary_pages_over_the_logical_budget()
    {
        var control = OperatorControl();
        var reply = new Wire.Reply
        {
            Format = 4,
            Control = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(control, PartitionParticipantWire.Json)),
            Page = new Wire.Page { GraphPresence = 1 }
        };
        var vertex = new Wire.Vertex { Id = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()) };
        vertex.Properties.Add(new Wire.Property { Name = "p", Value = new Wire.Value { Text = new string('\n', PartitionQueryLimits.ExchangePageBytes / 2) } });
        reply.Page.Vertices.Add(vertex);
        Assert.IsTrue(reply.CalculateSize() < PartitionQueryLimits.ExchangePageBytes);
        Assert.ThrowsExactly<InvalidOperationException>(() => PartitionQueryOperatorWire.Decode(reply.ToByteArray()));
    }

    [TestMethod]
    public void Native_profile_pages_reject_repeated_catalogues()
    {
        var data = new PartitionQueryData();
        var page = new PartitionQueryRelation(Catalog: new([], [], []), SemanticRows: []);
        data.AddNativeProfilePage(page);
        Assert.ThrowsExactly<InvalidDataException>(() => data.AddNativeProfilePage(page));
    }

    [TestMethod]
    public void Semantic_operator_page_retains_its_validated_budget_at_the_transport_boundary()
    {
        var concept = new SemanticTerm(new Uri("urn:budget:item"), SemanticTermRole.Concept);
        var property = new SemanticTerm(new Uri("urn:budget:text"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("urn:budget:id"), "id", SemanticValueKind.String);
        var subject = new SemanticEntityIdentity(concept, scheme, new StringSemanticValue("one"));
        var model = new SemanticModelReference(new Uri("urn:budget:model"), "1", SemanticModelKind.Vocabulary);
        var field = new SemanticFieldEvidence(Guid.NewGuid(), subject, property, new StringSemanticValue(""),
            new(new Uri("urn:budget:source"), "one", DateTimeOffset.UnixEpoch, model), new(1), null);
        PartitionQueryExchangePage Page(int length) => new(new(Fields: [field with { Value = new StringSemanticValue(new string('x', length)) }]), null);
        int emptyBytes = checked((int)PartitionProjectionValue.Measure(Page(0), PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options));
        var page = Page(PartitionQueryLimits.ExchangePageBytes - emptyBytes);
        long canonical = PartitionProjectionValue.Measure(page, PartitionQueryLimits.ExchangePageBytes, PartitionQueryJson.Options);
        Assert.AreEqual((long)PartitionQueryLimits.ExchangePageBytes, canonical);
        var value = new PartitionProjectionValue(page, PartitionQueryJson.Options);
        long domBytes = PartitionProjectionValue.Measure(value.Element, PartitionParticipantWire.MaximumResponseBytes);
        Assert.IsTrue(domBytes > canonical, "JSON DOM re-encoding must reproduce the rejected representation.");
        Assert.ThrowsExactly<InvalidOperationException>(() => PartitionProjectionValue.Measure(value.Element, PartitionQueryLimits.ExchangePageBytes));
        var (_, decoded) = PartitionQueryOperatorWire.Decode(PartitionQueryOperatorWire.Encode(OperatorControl(), new(true, value, false)));
        Assert.AreEqual(canonical, decoded.Value!.QueryPageBytes);
        var data = new PartitionQueryData();
        Assert.AreEqual(1, data.AddNativeProfilePage(((PartitionQueryExchangePage)decoded.Value.Native!).Relation));
        Assert.AreEqual(page.Relation.Fields![0], data.TypedRows<SemanticFieldEvidence>("fields").Single());
        Assert.ThrowsExactly<InvalidOperationException>(() => PartitionQueryOperatorWire.Encode(OperatorControl(), new(true, new(Page(PartitionQueryLimits.ExchangePageBytes - emptyBytes + 1), PartitionQueryJson.Options), false)));
    }

    private static PartitionQueryOperatorWire.Control OperatorControl()
    {
        var identity = new DatabasePartitionIdentity(Guid.NewGuid(), DatabaseProfileId.PropertyGraph, "routing", "a", Guid.NewGuid());
        var cut = new PartitionReadCut(Guid.NewGuid(), new(identity, new(3)), DateTimeOffset.UtcNow.AddSeconds(20));
        return new(identity, "authority", cut.Id, cut, true, false, null);
    }
}
