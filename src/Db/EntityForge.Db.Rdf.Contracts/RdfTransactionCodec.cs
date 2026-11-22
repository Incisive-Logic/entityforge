using System.Text;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

internal static class RdfTransactionCodec
{
    internal const int Version = 6;

    private static ReadOnlySpan<byte> Magic => "EFRD0006"u8;

    private static ReadOnlySpan<byte> LegacyMagic => "EFRD0005"u8;

    private const byte SubjectReadKind = 1, QueryReadKind = 2;
    private const int MaximumBytes = 512 * 1024 * 1024;
    internal const int MaximumItems = 10_000_000;
    private const int GuidBytes = 16;

    internal static byte[] Encode(RdfTransaction transaction, int maximumBytes = MaximumBytes)
    {
        if (maximumBytes is <= 0 or > MaximumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var stream = new EncodingStream(maximumBytes);
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        void Optional(string? text)
        {
            writer.Write(text is not null);
            if (text is not null)
            {
                writer.Write(text);
            }
        }
        void Term(RdfTerm? term)
        {
            writer.Write(term is not null);
            if (term is not null)
            {
                RdfPhysicalCodec.WriteTerm(writer, term);
            }
        }
        writer.Write(Magic);
        writer.Write(transaction.TransactionId.ToByteArray());
        writer.Write(transaction.IsInitialBulkLoad);
        Optional(transaction.SourceCommandHash);
        writer.Write(transaction.ReadVersion is not null);
        if (transaction.ReadVersion is { } version)
        {
            writer.Write(version.Value);
        }

        Optional(transaction.InterpretationFingerprint);
        writer.Write(transaction.Mutations.Count);
        foreach (var mutation in transaction.Mutations)
        {
            writer.Write(mutation switch
            {
                AssertRdfQuad => (byte)1,
                RetractRdfQuad => (byte)2,
                _ => throw new InvalidDataException("Unknown RDF mutation.")
            });
            var quad = mutation.Quad.Validate();
            RdfPhysicalCodec.WriteTerm(writer, quad.Subject);
            RdfPhysicalCodec.WriteTerm(writer, quad.Predicate);
            RdfPhysicalCodec.WriteTerm(writer, quad.Object);
            Term(quad.Graph);
            if (stream.Length > MaximumBytes)
            {
                throw new InvalidDataException("RDF transaction exceeds its byte bound.");
            }
        }
        writer.Write(transaction.ReadPredicates is not null);
        if (transaction.ReadPredicates is { } reads)
        {
            writer.Write(reads.Count);
            foreach (var predicate in reads)
            {
                if (predicate is RdfQueryStateRead query)
                {
                    if (query.Subjects.Count > MaximumItems)
                    {
                        throw new InvalidDataException("RDF query read exceeds its subject count bound.");
                    }

                    writer.Write(QueryReadKind);
                    RdfPhysicalCodec.WriteTerm(writer, query.Predicate);
                    RdfPhysicalCodec.WriteTerm(writer, query.Object);
                    writer.Write(query.Subjects.Count);
                    foreach (var subject in query.Subjects)
                    {
                        RdfPhysicalCodec.WriteTerm(writer, subject);
                    }

                    writer.Write(query.ExpectedFingerprint);
                    continue;
                }
                writer.Write(SubjectReadKind);
                if (predicate is not RdfSubjectStateRead read)
                {
                    throw new InvalidDataException("Unknown RDF predicate.");
                }

                RdfPhysicalCodec.WriteTerm(writer, read.Subject);
                Term(read.Graph);
                writer.Write(read.MatchDefaultGraphOnly);
                writer.Write(read.ExpectedFingerprint);
            }
        }
        writer.Flush();
        if (stream.Length > MaximumBytes)
        {
            throw new InvalidDataException("RDF transaction exceeds its byte bound.");
        }

        return stream.ToArray();
    }

    internal static RdfTransaction DecodeTransaction(Guid transactionId, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumBytes || !(payload.StartsWith(Magic) || payload.StartsWith(LegacyMagic)))
        {
            throw new InvalidDataException("Unknown or oversized RDF transaction.");
        }

        bool legacy = payload.StartsWith(LegacyMagic);
        using var stream = new MemoryStream(payload.ToArray(), false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        bool Flag()
        {
            byte value = reader.ReadByte();
            if (value > 1)
            {
                throw new InvalidDataException("Invalid RDF flag.");
            }

            return value == 1;
        }
        string? Optional() => Flag() ? reader.ReadString() : null;
        RdfTerm? Term() => Flag() ? RdfPhysicalCodec.ReadTerm(reader, 0) : null;
        int Count()
        {
            int n = reader.ReadInt32();
            if (n < 0 || n > MaximumItems || n > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid RDF collection count.");
            }

            return n;
        }
        try
        {
            stream.Position = Magic.Length;
            var id = reader.ReadBytes(GuidBytes);
            if (id.Length != GuidBytes || new Guid(id) != transactionId)
            {
                throw new InvalidDataException("RDF transaction identity differs.");
            }

            bool bulk = Flag();
            string? hash = Optional();
            DatabaseVersion? version = Flag() ? new(reader.ReadInt64()) : null;
            string? interpretation = Optional();
            var mutations = new RdfMutation[Count()];
            for (int i = 0; i < mutations.Length; i++)
            {
                byte kind = reader.ReadByte();
                var subject = RdfPhysicalCodec.ReadTerm(reader, 0);
                var predicate = RdfPhysicalCodec.ReadTerm(reader, 0) as RdfIri ?? throw new InvalidDataException("RDF predicate must be IRI.");
                var quad = new RdfQuad(subject, predicate, RdfPhysicalCodec.ReadTerm(reader, 0), Term()).Validate();
                mutations[i] = kind switch
                {
                    1 => new AssertRdfQuad(quad),
                    2 => new RetractRdfQuad(quad),
                    _ => throw new InvalidDataException("Invalid RDF mutation.")
                };
            }
            RdfReadPredicate[]? reads = null;
            if (Flag())
            {
                reads = new RdfReadPredicate[Count()];
                for (int i = 0; i < reads.Length; i++)
                {
                    byte kind = legacy ? SubjectReadKind : reader.ReadByte();
                    if (kind == SubjectReadKind)
                    {
                        reads[i] = new RdfSubjectStateRead(RdfPhysicalCodec.ReadTerm(reader, 0), Term(), Flag(), reader.ReadString());
                    }
                    else if (kind == QueryReadKind)
                    {
                        var predicate = RdfPhysicalCodec.ReadTerm(reader, 0) as RdfIri ?? throw new InvalidDataException("Query predicate must be IRI.");
                        var value = RdfPhysicalCodec.ReadTerm(reader, 0);
                        var subjects = new RdfTerm[Count()];
                        for (int j = 0; j < subjects.Length; j++)
                        {
                            subjects[j] = RdfPhysicalCodec.ReadTerm(reader, 0);
                        }

                        reads[i] = new RdfQueryStateRead(predicate, value, subjects, reader.ReadString());
                    }
                    else
                    {
                        throw new InvalidDataException("Unknown RDF read predicate.");
                    }
                }
            }
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("RDF transaction has trailing bytes.");
            }

            return new(transactionId, mutations, bulk, hash, version, reads, interpretation);
        }
        catch (Exception e) when (e is EndOfStreamException or DecoderFallbackException or ArgumentException)
        {
            throw new InvalidDataException("Invalid RDF transaction.", e);
        }
    }

    // Check before growing the backing array. The returned ToArray copy and
    // caller-owned inputs are separate from this encoded-payload capacity bound.
    private sealed class EncodingStream(int maximumBytes) : MemoryStream
    {
        private void Prepare(int count)
        {
            if (count < 0 || Position > maximumBytes - (long)count)
            {
                throw new InvalidDataException("RDF transaction exceeds its byte bound.");
            }

            int required = (int)Position + count;
            if (required > Capacity)
            {
                Capacity = (int)Math.Min(maximumBytes, Math.Max(required, Math.Max(256L, Capacity * 2L)));
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Prepare(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Prepare(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Prepare(1);
            base.WriteByte(value);
        }
    }
}
