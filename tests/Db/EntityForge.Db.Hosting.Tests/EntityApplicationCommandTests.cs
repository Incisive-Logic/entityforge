using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class EntityApplicationCommandTests
{
    [TestMethod]
    public void Framing_binds_destination_profile_operation_action_and_exact_parts()
    {
        Guid database = Guid.NewGuid(), operation = Guid.NewGuid();
        var envelope = EntityApplicationCommandCodec.Encode(database, DatabaseProfileId.Rdf, operation, "apply", [[1, 2], [3]]);
        var parts = EntityApplicationCommandCodec.Decode(envelope, database, DatabaseProfileId.Rdf, operation, "apply");
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, parts[0]);
        CollectionAssert.AreEqual(new byte[] { 3 }, parts[1]);
        Assert.ThrowsException<InvalidDataException>(() => EntityApplicationCommandCodec.Decode(envelope, Guid.NewGuid(), DatabaseProfileId.Rdf, operation, "apply"));
        Assert.ThrowsException<InvalidDataException>(() => EntityApplicationCommandCodec.Decode(envelope, database, DatabaseProfileId.Semantic, operation, "apply"));
        Assert.ThrowsException<InvalidDataException>(() => EntityApplicationCommandCodec.Decode(envelope, database, DatabaseProfileId.Rdf, Guid.NewGuid(), "apply"));
        Assert.ThrowsException<InvalidDataException>(() => EntityApplicationCommandCodec.Decode(envelope, database, DatabaseProfileId.Rdf, operation, "preparebatch"));
        envelope[^1] ^= 1;
        Assert.ThrowsException<InvalidDataException>(() => EntityApplicationCommandCodec.Decode(envelope, database, DatabaseProfileId.Rdf, operation, "apply"));
    }

    [TestMethod]
    public void Restored_native_changes_cannot_bypass_factory_rules()
    {
        Guid operation = Guid.NewGuid();
        var graph = new GraphChangeSession.Definition(1, operation,
            GraphTransactionCodec.Encode(new(operation, [], IsInitialBulkLoad: true)), null, null, null);
        Assert.ThrowsException<ArgumentException>(() => GraphChangeSession.RestoreRequest(JsonSerializer.SerializeToUtf8Bytes(graph)));
        var rdf = new RdfChangeSession.Definition(1, operation,
            RdfTransactionCodec.Encode(new(operation, [], IsInitialBulkLoad: true)), null);
        Assert.ThrowsException<ArgumentException>(() => RdfChangeSession.RestoreRequest(JsonSerializer.SerializeToUtf8Bytes(rdf)));
        var subject = new RdfIri("urn:test:subject");
        var invalid = new RdfChangeSession.Definition(1, operation, null,
            [new(subject, null, [new(new RdfIri("urn:outside"), new("urn:p"), new RdfLiteral("value"))])]);
        Assert.ThrowsException<ArgumentException>(() => RdfChangeSession.RestoreRequest(JsonSerializer.SerializeToUtf8Bytes(invalid)));
    }
}
