using System.Text.Json;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Query.Planning;
using Google.Protobuf;
using Wire = EntityForge.Db.Distributed.Hosting.QueryWire;

namespace EntityForge.Db.Partition.Tests;

public sealed partial class PartitionOwnedNodeTests
{
    [TestMethod]
    public void Projection_columns_are_immutable_and_empty_rows_keep_their_occurrence()
    {
        var requested = new List<string> { "wanted" };
        var columns = new QueryProjectionColumns<string>(requested, StringComparer.Ordinal);
        requested.Add("private");
        var input = new Dictionary<string, int> { ["wanted"] = 1, ["private"] = 2 };
        CollectionAssert.AreEqual(new[] { "wanted" }, columns.Project(input).Keys.ToArray());
        Assert.AreEqual(2, input.Count);
        Assert.AreEqual(0, new QueryProjectionColumns<string>([]).Project(input).Count);
        Assert.AreEqual(2, new QueryProjectionColumns<string>(null).Project(input).Count);
        var joined = columns.Union(new QueryProjectionColumns<string>(["second"], StringComparer.Ordinal));
        Assert.IsTrue(joined.Contains("wanted") && joined.Contains("second"));
        Assert.IsFalse(joined.Contains("private"));
    }

    [TestMethod]
    public void Scalar_operator_wire_preserves_rows_without_constructing_entities()
    {
        var row = new GraphFragmentRow(new(Guid.NewGuid()), new Dictionary<string, DatabaseValue>
        {
            ["requested"] = new DatabaseValue.String("\"quoted\" 日本"),
            ["json"] = new DatabaseValue.Json("{\"v\":null}")
        });
        var page = new PartitionQueryExchangePage(new(ScalarRows: [row]), null);
        var bytes = PartitionQueryOperatorWire.Encode(OperatorControl(), new(true, new(page), false));
        var decoded = (PartitionQueryExchangePage)PartitionQueryOperatorWire.Decode(bytes).Result.Value!.Native!;
        Assert.IsNull(decoded.Relation.Vertices);
        Assert.IsNull(decoded.Relation.GraphBindings);
        Assert.AreEqual(JsonSerializer.Serialize(row, PartitionQueryJson.Options), JsonSerializer.Serialize(decoded.Relation.ScalarRows!.Single(), PartitionQueryJson.Options));
        var malformed = Wire.Reply.Parser.ParseFrom(bytes);
        malformed.Page.Vertices[0].Labels.Add("fabricated-entity");
        Assert.ThrowsExactly<InvalidDataException>(() => PartitionQueryOperatorWire.Decode(malformed.ToByteArray()));
    }

    [TestMethod]
    public async Task Projected_Rdf_rows_keep_duplicate_occurrences_and_drop_local_only_columns()
    {
        await WithProfileFragments(DatabaseProfileId.Rdf, async (control, candidate, _, _, _, nodes, _) =>
        {
            await candidate.UpdateQueryAsync(new(candidate.Generation, candidate.Profile, EntityOperationIds.New(), "sparql",
                "INSERT DATA { <urn:a> <urn:n> 2 . <urn:b> <urn:n> 2 . <urn:a> <urn:private> \"payload-a\" . <urn:b> <urn:private> \"payload-b\" . }"), default);
            const string query = "SELECT ?n WHERE { ?s <urn:n> ?n . ?s <urn:private> ?private FILTER (?n = 2) }";
            var baseline = await control.QueryAsync(new(candidate.Generation, candidate.Profile, "sparql", query), default);
            var result = await candidate.QueryAsync(new(candidate.Generation, candidate.Profile, "sparql", query), default);
            Assert.AreEqual(Canonical(baseline.Value), Canonical(result.Value));
            Assert.AreEqual(2, result.Value.GetProperty("bindings").GetArrayLength());
            var rows = new List<RdfBindingFragment>();
            foreach (var node in nodes)
            {
                var relation = await node.CaptureQueryRelationAsync(node.Kernel.CurrentVersion,
                    new("query-exchange", QueryExchange: new("sparql", query, ProfileFragments: true)), default);
                rows.AddRange(relation.RdfBindings!);
            }
            Assert.AreEqual(2, rows.Count);
            foreach (var row in rows)
            {
                CollectionAssert.AreEqual(new[] { "n" }, row.Bindings.Keys.ToArray());
                Assert.IsNotNull(row.OwnerSubject);
                Assert.IsNotNull(row.Occurrence);
            }
            var validate = RdfSparql.CreateBindingFragmentOwnerValidator(query);
            Assert.ThrowsExactly<InvalidDataException>(() => validate(rows[0] with { Occurrence = -1 }));
            Assert.ThrowsExactly<InvalidDataException>(() => validate(rows[0] with { Bindings = new() { ["private"] = new RdfIri("urn:bad") } }));
        });
    }
}
