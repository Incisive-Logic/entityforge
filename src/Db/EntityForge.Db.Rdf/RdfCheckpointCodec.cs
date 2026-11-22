using System.Text;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage.Local;

namespace EntityForge.Db.Rdf;

/// <summary>Complete RDF state in a bounded, checksummed native binary checkpoint.</summary>
internal static class RdfCheckpointCodec
{
    private const int GuidBytes = 16;
    private const byte PayloadVersion = 2;
    private const int MinimumQuadBytes = 6;
    private const int MaximumPredicateEntries = 1024;
    private const int MaximumPredicateUtf8Bytes = 64 * 1024;
    private const int MinimumReplayBytes = 29;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal sealed record State(IReadOnlyList<RdfQuad> Quads,
        IReadOnlyDictionary<Guid, RdfProfileRuntime.RdfCommandReplay> CommandReplays);

    internal static ReadOnlyMemory<byte> Encode(IReadOnlyCollection<RdfQuad> quads,
        IReadOnlyDictionary<Guid, RdfProfileRuntime.RdfCommandReplay> replays,
        CancellationToken token = default) => DatabaseCheckpointCompression.Encode(stream =>
    {
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write(PayloadVersion);
        writer.Write(quads.Count);
        var predicates = new Dictionary<string, int>(StringComparer.Ordinal);
        int predicateBytes = 0;
        foreach (var quad in quads)
        {
            token.ThrowIfCancellationRequested();
            RdfPhysicalCodec.WriteTerm(writer, quad.Subject);
            if (predicates.TryGetValue(quad.Predicate.Value, out int reference))
            {
                writer.Write7BitEncodedInt(reference);
            }
            else
            {
                writer.Write7BitEncodedInt(0);
                writer.Write(quad.Predicate.Value);
                int bytes = Utf8.GetByteCount(quad.Predicate.Value);
                if (predicates.Count < MaximumPredicateEntries && bytes <= MaximumPredicateUtf8Bytes - predicateBytes)
                {
                    predicates.Add(quad.Predicate.Value, predicates.Count + 1);
                    predicateBytes += bytes;
                }
            }
            RdfPhysicalCodec.WriteTerm(writer, quad.Object);
            writer.Write(quad.Graph is not null);
            if (quad.Graph is not null)
            {
                RdfPhysicalCodec.WriteTerm(writer, quad.Graph);
            }
        }
        writer.Write(replays.Count);
        foreach (var (id, replay) in replays)
        {
            token.ThrowIfCancellationRequested();
            writer.Write(id.ToByteArray());
            writer.Write(replay.CommandHash);
            writer.Write(replay.MutationCount);
            writer.Write(replay.Version.Value);
        }
        token.ThrowIfCancellationRequested();
    }, "EFRC"u8, token);

    internal static State Decode(ReadOnlyMemory<byte> frame, CancellationToken token = default)
    {
        byte[] payload = DatabaseCheckpointCompression.Decode(frame.Span, "EFRC"u8, token);
        try
        {
            using var stream = new MemoryStream(payload, 0, payload.Length, false, true);
            using var reader = new BoundedReader(stream);
            byte payloadVersion = reader.ReadByte();
            if (payloadVersion is not (1 or PayloadVersion))
            {
                throw new InvalidDataException("Unsupported RDF checkpoint payload.");
            }

            int count = ReadCount(reader, MinimumQuadBytes);
            var quads = new List<RdfQuad>();
            var distinct = new HashSet<RdfQuad>();
            var predicates = new List<RdfIri>();
            int predicateBytes = 0;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                RdfTerm subject = RdfPhysicalCodec.ReadTerm(reader, 0);
                RdfIri predicate;
                if (payloadVersion == 1)
                {
                    predicate = RdfPhysicalCodec.ReadTerm(reader, 0) as RdfIri ??
                        throw new InvalidDataException("An RDF checkpoint predicate must be an IRI.");
                }
                else
                {
                    int reference = reader.Read7BitEncodedInt();
                    if (reference < 0 || reference > predicates.Count)
                    {
                        throw new InvalidDataException("An RDF checkpoint predicate reference is invalid.");
                    }
                    if (reference != 0)
                    {
                        predicate = predicates[reference - 1];
                    }
                    else
                    {
                        predicate = new RdfIri(reader.ReadString());
                        int bytes = Utf8.GetByteCount(predicate.Value);
                        if (predicates.Count < MaximumPredicateEntries && bytes <= MaximumPredicateUtf8Bytes - predicateBytes)
                        {
                            predicates.Add(predicate);
                            predicateBytes += bytes;
                        }
                    }
                }
                RdfTerm value = RdfPhysicalCodec.ReadTerm(reader, 0);
                byte graphFlag = reader.ReadByte();
                if (graphFlag > 1)
                {
                    throw new InvalidDataException("Invalid RDF checkpoint graph flag.");
                }

                RdfTerm? graph = graphFlag == 1 ? RdfPhysicalCodec.ReadTerm(reader, 0) : null;
                var quad = new RdfQuad(subject, predicate, value, graph).Validate();
                if (!distinct.Add(quad))
                {
                    throw new InvalidDataException("Duplicate RDF checkpoint quad.");
                }

                quads.Add(quad);
            }
            int replayCount = ReadCount(reader, MinimumReplayBytes);
            var replays = new Dictionary<Guid, RdfProfileRuntime.RdfCommandReplay>();
            for (int i = 0; i < replayCount; i++)
            {
                token.ThrowIfCancellationRequested();
                byte[] idBytes = reader.ReadBytes(GuidBytes);
                if (idBytes.Length != GuidBytes)
                {
                    throw new EndOfStreamException();
                }

                var id = new Guid(idBytes);
                string hash = reader.ReadString();
                int mutations = reader.ReadInt32();
                long version = reader.ReadInt64();
                if (mutations < 0 || version < 0 || !replays.TryAdd(id, new(hash, mutations, new(version))))
                {
                    throw new InvalidDataException("Invalid or duplicate RDF checkpoint command replay.");
                }
            }
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Trailing RDF checkpoint data.");
            }

            token.ThrowIfCancellationRequested();
            return new(quads, replays);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid RDF checkpoint payload.", error);
        }
    }

    private static int ReadCount(BinaryReader reader, int minimumBytes)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumBytes)
        {
            throw new InvalidDataException("RDF checkpoint count exceeds its remaining payload.");
        }

        return count;
    }

    private sealed class BoundedReader(Stream stream) : BinaryReader(stream, Utf8)
    {
        public override string ReadString()
        {
            int length = Read7BitEncodedInt();
            if (length < 0 || length > BaseStream.Length - BaseStream.Position)
            {
                throw new InvalidDataException("RDF checkpoint string exceeds its remaining payload.");
            }

            return DatabaseUtf8Read.Read(this, length, Utf8);
        }
    }
}
