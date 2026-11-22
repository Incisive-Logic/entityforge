using Google.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Semantics.Protobuf;

/// <summary>
/// Encodes and decodes the versioned protobuf transport representation without exposing generated
/// messages as the semantic contract.
/// </summary>
public sealed class SemanticChangeSessionTransportCodec
{
    /// <summary>The semantic session transport schema version.</summary>
    public const int SchemaVersion = 2;
    /// <summary>The version of the page-content hash domain.</summary>
    public const int ContentHashSchemaVersion = 2;
    /// <summary>The version of the operation hash domain.</summary>
    public const int OperationHashSchemaVersion = 1;

    /// <summary>Encodes a semantic entity identity as Protocol Buffers bytes.</summary>
    public byte[] SerializeIdentity(SemanticEntityIdentity value) => SemanticChangeProtobufConverter.ToMessage(value).ToByteArray();

    /// <summary>Decodes and validates a semantic identity from its Protocol Buffers representation.</summary>
    public SemanticEntityIdentity DeserializeIdentity(ReadOnlySpan<byte> payload) => SemanticChangeProtobufConverter.ToDomain(SemanticEntityIdentityMessage.Parser.ParseFrom(payload));

    /// <summary>Encodes semantic provenance as Protocol Buffers bytes.</summary>
    public byte[] SerializeProvenance(SemanticProvenance value) => SemanticChangeProtobufConverter.ToMessage(value).ToByteArray();

    /// <summary>Encodes the supplied semantic session start, page or close envelope.</summary>
    public byte[] Serialize(SemanticChangeSessionStart value) =>
        SemanticChangeProtobufConverter.ToMessage(value).ToByteArray();

    /// <summary>Encodes the supplied semantic session start, page or close envelope.</summary>
    public byte[] Serialize(SemanticChangeSessionPage value) =>
        SemanticChangeProtobufConverter.ToMessage(value).ToByteArray();

    /// <summary>Captures a page once so its length can be admitted before streaming its bytes.</summary>
    public PreparedPage PrepareSerialization(SemanticChangeSessionPage value) =>
        new(SemanticChangeProtobufConverter.ToMessage(value));

    /// <summary>Owns a captured protobuf page whose encoded length is known before streaming.</summary>
    public sealed class PreparedPage
    {
        private readonly SemanticChangeSessionPageMessage _message;

        internal PreparedPage(SemanticChangeSessionPageMessage message)
        {
            _message = message;
            Length = message.CalculateSize();
            if (Length < 0)
            {
                throw new InvalidDataException("Prepared Semantic page has an invalid calculated size.");
            }
        }

        /// <summary>Gets the exact encoded page length used for byte-budget admission.</summary>
        public int Length
        {
            get;
        }

        /// <summary>Writes captured bytes without flushing or closing the caller's stream.</summary>
        public void WriteTo(Stream destination)
        {
            ArgumentNullException.ThrowIfNull(destination);
            // Protobuf finishes its own buffer, but framing/compression owns the outer flush boundary.
            using var sink = new PageWriteStream(destination, Length);
            _message.WriteTo(sink);
            if (sink.Written != Length)
            {
                throw new InvalidDataException("Prepared Semantic page length differs from its calculated size.");
            }
        }

        private sealed class PageWriteStream(Stream destination, int expectedLength) : Stream
        {
            public long Written
            {
                get; private set;
            }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanWrite => destination.CanWrite;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (buffer.Length > expectedLength - Written)
                {
                    throw new InvalidDataException("Prepared Semantic page exceeds its calculated size.");
                }

                destination.Write(buffer);
                Written += buffer.Length;
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }

    /// <summary>Encodes the supplied semantic session start, page or close envelope.</summary>
    public byte[] Serialize(SemanticChangeSessionClose value) =>
        SemanticChangeProtobufConverter.ToMessage(value).ToByteArray();

    /// <summary>Decodes and validates a semantic session start envelope.</summary>
    public SemanticChangeSessionStart DeserializeStart(ReadOnlySpan<byte> payload) =>
        SemanticChangeSessionStartMessage.Parser.ParseFrom(payload).ToDomain();

    /// <summary>Decodes and validates a semantic session page.</summary>
    public SemanticChangeSessionPage DeserializePage(ReadOnlySpan<byte> payload) =>
        SemanticChangeSessionPageMessage.Parser.ParseFrom(payload).ToDomain();

    /// <summary>Decodes and validates a semantic session closing envelope.</summary>
    public SemanticChangeSessionClose DeserializeClose(ReadOnlySpan<byte> payload) =>
        SemanticChangeSessionCloseMessage.Parser.ParseFrom(payload).ToDomain();

    /// <summary>Computes content and operation hashes with one page serialization pass.</summary>
    public (string ContentHash, string OperationHash) ComputeHashes(SemanticChangeSessionStart start,
        IEnumerable<SemanticChangeSessionPage> pages) => SemanticChangeSessionContentHasher.ComputeBoth(start, pages);

    /// <summary>Computes the versioned content digest of pages ordered by page number.</summary>
    public string ComputeContentHash(IEnumerable<SemanticChangeSessionPage> pages) =>
        SemanticChangeSessionContentHasher.Compute(pages);

    /// <summary>Computes the versioned operation digest including the start envelope and ordered pages.</summary>
    public string ComputeOperationHash(SemanticChangeSessionStart start,
        IEnumerable<SemanticChangeSessionPage> pages) =>
        SemanticChangeSessionContentHasher.ComputeOperation(start, pages);
}
