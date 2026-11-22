using System.Text;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class RdfCheckpointCodecTests
{
    [TestMethod]
    public void Predicate_dictionary_reduces_expanded_bytes_and_preserves_legacy_reading()
    {
        var predicate = new RdfIri("urn:predicate:" + new string('p', 100));
        var quads = Enumerable.Range(0, 20000).Select(index => new RdfQuad(new RdfIri($"urn:s:{index}"),
            predicate, new RdfLiteral("value \u03bb"), index % 2 == 0 ? new RdfIri("urn:graph") : null)).ToArray();
        var current = RdfCheckpointCodec.Encode(quads, new Dictionary<Guid, RdfProfileRuntime.RdfCommandReplay>());
        var legacy = Legacy(quads);
        int currentBytes = DatabaseCheckpointCompression.Decode(current.Span, "EFRC"u8).Length;
        int legacyBytes = DatabaseCheckpointCompression.Decode(legacy.Span, "EFRC"u8).Length;
        Assert.IsTrue(legacyBytes - currentBytes > 2_000_000, "Repeated vocabulary must save expanded bytes, not merely compressed bytes.");
        CollectionAssert.AreEqual(quads, RdfCheckpointCodec.Decode(current).Quads.ToArray());
        CollectionAssert.AreEqual(quads, RdfCheckpointCodec.Decode(legacy).Quads.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Predicate_dictionary_limits_entries_and_utf8_bytes_without_losing_uncached_terms(bool byteLimit)
    {
        int count = byteLimit ? 80 : 1050;
        var predicates = Enumerable.Range(0, count).Select(index => new RdfIri($"urn:p:{index}:" +
            (byteLimit ? new string('\u03bb', 1024) : ""))).ToArray();
        var all = predicates.Concat(new[] { predicates[0], predicates[^1] }).ToArray();
        var quads = all.Select((predicate, index) => new RdfQuad(new RdfIri($"urn:s:{index}"), predicate, new RdfLiteral("x"))).ToArray();
        Guid operation = Guid.NewGuid();
        var replays = new Dictionary<Guid, RdfProfileRuntime.RdfCommandReplay> { [operation] = new(new string('A', 64), 3, new(7)) };
        var frame = RdfCheckpointCodec.Encode(quads, replays);
        var restored = RdfCheckpointCodec.Decode(frame);
        CollectionAssert.AreEqual(quads, restored.Quads.ToArray());
        Assert.AreEqual(replays[operation], restored.CommandReplays[operation]);
        using var reader = new BinaryReader(new MemoryStream(DatabaseCheckpointCompression.Decode(frame.Span, "EFRC"u8)), Encoding.UTF8);
        Assert.AreEqual((byte)2, reader.ReadByte());
        Assert.AreEqual(quads.Length, reader.ReadInt32());
        for (int index = 0; index < quads.Length; index++)
        {
            _ = RdfPhysicalCodec.ReadTerm(reader, 0);
            int reference = reader.Read7BitEncodedInt();
            Assert.AreEqual(index == count ? 1 : 0, reference,
                "Only the first cached predicate should be referenced; overflow vocabulary stays inline.");
            if (reference == 0)
            {
                _ = reader.ReadString();
            }
            _ = RdfPhysicalCodec.ReadTerm(reader, 0);
            Assert.IsFalse(reader.ReadBoolean());
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(-1)]
    [DataRow(int.MaxValue)]
    public void Predicate_reference_cannot_address_an_unpopulated_dictionary(int reference)
    {
        var frame = DatabaseCheckpointCompression.Encode(stream =>
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write((byte)2);
            writer.Write(1);
            RdfPhysicalCodec.WriteTerm(writer, new RdfIri("urn:s"));
            writer.Write7BitEncodedInt(reference);
            RdfPhysicalCodec.WriteTerm(writer, new RdfLiteral("x"));
            writer.Write(false);
            writer.Write(0);
        }, "EFRC"u8);
        Assert.ThrowsExactly<InvalidDataException>(() => RdfCheckpointCodec.Decode(frame));
    }

    private static ReadOnlyMemory<byte> Legacy(IReadOnlyList<RdfQuad> quads) => DatabaseCheckpointCompression.Encode(stream =>
    {
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        writer.Write((byte)1);
        writer.Write(quads.Count);
        foreach (var quad in quads)
        {
            RdfPhysicalCodec.WriteTerm(writer, quad.Subject);
            RdfPhysicalCodec.WriteTerm(writer, quad.Predicate);
            RdfPhysicalCodec.WriteTerm(writer, quad.Object);
            writer.Write(quad.Graph is not null);
            if (quad.Graph is not null)
            {
                RdfPhysicalCodec.WriteTerm(writer, quad.Graph);
            }
        }
        writer.Write(0);
    }, "EFRC"u8);
}
