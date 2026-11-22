using System.Text;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Compact, deterministic encoding shared by RDF native transactions and the physical access path.</summary>
internal static class RdfPhysicalCodec
{
    private const byte FormatVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MaximumRetainedEncodingBufferBytes = 64 * 1024;
    [ThreadStatic] private static EncodingBuffer? _encodingBuffer;

    // One identity for native write reservations, scoped reads and their diagnostics.
    internal static string ResourceIdentity(RdfTerm term) => Convert.ToHexString(EncodeTerm(term));

    public static byte[] EncodeTerm(RdfTerm term)
    {
        EncodingBuffer buffer = AcquireEncodingBuffer();
        try
        {
            buffer.Writer.Write(FormatVersion);
            WriteTerm(buffer.Writer, term);
            return CompleteEncoding(buffer);
        }
        finally
        {
            ReleaseOversizedBuffer(buffer);
        }
    }

    public static byte[] EncodeQuad(RdfQuad quad)
    {
        EncodingBuffer buffer = AcquireEncodingBuffer();
        try
        {
            buffer.Writer.Write(FormatVersion);
            WriteTerm(buffer.Writer, quad.Subject);
            WriteTerm(buffer.Writer, quad.Predicate);
            WriteTerm(buffer.Writer, quad.Object);
            buffer.Writer.Write(quad.Graph is not null);
            if (quad.Graph is not null)
            {
                WriteTerm(buffer.Writer, quad.Graph);
            }

            return CompleteEncoding(buffer);
        }
        finally
        {
            ReleaseOversizedBuffer(buffer);
        }
    }

    private static void ReleaseOversizedBuffer(EncodingBuffer buffer)
    {
        // Failed encodes must obey the same retention bound as successful ones.
        if (buffer.Stream.Capacity > MaximumRetainedEncodingBufferBytes)
        {
            _encodingBuffer = null;
        }
    }

    private static EncodingBuffer AcquireEncodingBuffer()
    {
        EncodingBuffer buffer = _encodingBuffer ??= new();
        buffer.Stream.Position = 0;
        buffer.Stream.SetLength(0);
        return buffer;
    }

    private static byte[] CompleteEncoding(EncodingBuffer buffer)
    {
        buffer.Writer.Flush();
        byte[] encoded = buffer.Stream.ToArray();
        return encoded;
    }

    public static RdfQuad DecodeQuad(byte[] payload)
    {
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, Utf8, leaveOpen: false);
            RequireVersion(reader);
            RdfTerm subject = ReadTerm(reader, 0);
            RdfIri predicate = ReadTerm(reader, 0) as RdfIri ??
                throw new InvalidDataException("An RDF physical-index predicate is not an IRI.");
            RdfTerm @object = ReadTerm(reader, 0);
            RdfTerm? graph = reader.ReadByte() switch
            {
                0 => null,
                1 => ReadTerm(reader, 0),
                _ => throw new InvalidDataException("Invalid RDF graph flag.")
            };
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("An RDF physical-index value has trailing data.");
            }

            return new RdfQuad(subject, predicate, @object, graph).Validate();
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
        {
            throw new InvalidDataException("An RDF physical-index value is invalid.", exception);
        }
    }

    internal static void WriteTerm(BinaryWriter writer, RdfTerm term)
    {
        switch (term)
        {
            case RdfIri iri:
                writer.Write((byte)TermKind.Iri);
                writer.Write(iri.Value);
                break;
            case RdfBlankNode blank:
                writer.Write((byte)TermKind.BlankNode);
                writer.Write(blank.Identifier);
                break;
            case RdfLiteral literal:
                writer.Write((byte)TermKind.Literal);
                writer.Write(literal.LexicalForm);
                writer.Write(literal.Datatype ?? string.Empty);
                writer.Write(literal.Language ?? string.Empty);
                break;
            case RdfTripleTerm triple:
                writer.Write((byte)TermKind.Triple);
                WriteTerm(writer, triple.Subject);
                WriteTerm(writer, triple.Predicate);
                WriteTerm(writer, triple.Object);
                break;
            default:
                throw new InvalidDataException($"Unsupported RDF term type '{term.GetType().Name}'.");
        }
    }

    internal static RdfTerm ReadTerm(BinaryReader reader, int depth)
    {
        if (depth > RdfTripleTerm.MaximumNestingDepth)
        {
            throw new InvalidDataException("An RDF-star physical term exceeds the nesting limit.");
        }

        return (TermKind)reader.ReadByte() switch
        {
            TermKind.Iri => DecodedIris.Get(reader.ReadString()),
            TermKind.BlankNode => new RdfBlankNode(reader.ReadString()),
            TermKind.Literal => ReadLiteral(reader),
            TermKind.Triple => ReadTriple(reader, depth),
            _ => throw new InvalidDataException("An RDF physical-index term kind is invalid.")
        };
    }

    private static readonly DatabaseIdentifierCache<RdfIri> DecodedIris = new(text => new RdfIri(text));

    private static RdfLiteral ReadLiteral(BinaryReader reader)
    {
        string lexicalForm = reader.ReadString();
        string datatype = DatabaseDecodedIdentifiers.Text(reader.ReadString());
        string language = DatabaseDecodedIdentifiers.Text(reader.ReadString());
        return new RdfLiteral(lexicalForm,
            datatype.Length == 0 ? null : datatype,
            language.Length == 0 ? null : language);
    }

    private static RdfTripleTerm ReadTriple(BinaryReader reader, int depth)
    {
        int nestedDepth = checked(depth + 1);
        RdfTerm subject = ReadTerm(reader, nestedDepth);
        RdfIri predicate = ReadTerm(reader, nestedDepth) as RdfIri ??
            throw new InvalidDataException("An RDF-star predicate is not an IRI.");
        return new(subject, predicate, ReadTerm(reader, nestedDepth));
    }

    private static void RequireVersion(BinaryReader reader)
    {
        byte version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported RDF physical-index format version '{version}'.");
        }
    }

    private enum TermKind : byte
    {
        Iri = 1, BlankNode = 2, Literal = 3, Triple = 4
    }

    private sealed class EncodingBuffer
    {
        public MemoryStream Stream { get; } = new();

        public BinaryWriter Writer
        {
            get;
        }

        public EncodingBuffer() => Writer = new(Stream, Utf8, leaveOpen: true);
    }
}
