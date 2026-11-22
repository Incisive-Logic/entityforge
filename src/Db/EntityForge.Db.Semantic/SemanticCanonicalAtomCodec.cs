using System.Text;
using System.Runtime.InteropServices;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed class SemanticCanonicalAtomCodec
{
    public const byte FormatVersion = 5;
    private readonly SemanticAtomStorageCodec _storage = new();

    public SemanticAtomRecord Encode(SemanticTerm value) => Encode(SemanticAtomKind.Term, value);

    public SemanticAtomRecord Encode(SemanticValue value) => Encode(SemanticAtomKind.Value, value);

    public SemanticAtomRecord Encode(SemanticEntityIdentity value) => Encode(SemanticAtomKind.Identity, value);

    public SemanticAtomRecord Encode(SemanticProvenance value) => Encode(SemanticAtomKind.Provenance, value);

    public SemanticTerm DecodeTerm(SemanticAtomRecord atom) => Decode<SemanticTerm>(atom, SemanticAtomKind.Term);

    public SemanticValue DecodeValue(SemanticAtomRecord atom) => Decode<SemanticValue>(atom, SemanticAtomKind.Value);

    public SemanticEntityIdentity DecodeIdentity(SemanticAtomRecord atom) =>
        Decode<SemanticEntityIdentity>(atom, SemanticAtomKind.Identity);

    public SemanticProvenance DecodeProvenance(SemanticAtomRecord atom)
    {
        Validate(atom, SemanticAtomKind.Provenance);
        return SemanticProvenanceBinaryCodec.Decode(atom.CanonicalBytes[sizeof(byte)..]);
    }

    private SemanticAtomRecord Encode<T>(SemanticAtomKind kind, T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true);
        writer.Write(FormatVersion);
        switch (value)
        {
            case SemanticTerm term:
                SemanticValueBinaryCodec.Term(writer, term);
                break;
            case SemanticValue item:
                SemanticValueBinaryCodec.Write(writer, item);
                break;
            case SemanticEntityIdentity identity:
                SemanticValueBinaryCodec.Identity(writer, identity);
                break;
            case SemanticProvenance provenance:
                SemanticProvenanceBinaryCodec.Write(writer, provenance);
                break;
            default:
                throw new InvalidDataException("Unsupported native Semantic atom.");
        }
        writer.Flush();
        // This invocation owns the buffer; only the written prefix is published.
        return _storage.Create(kind, stream.GetBuffer().AsMemory(0, checked((int)stream.Length)));
    }

    private T Decode<T>(SemanticAtomRecord atom, SemanticAtomKind expectedKind)
    {
        Validate(atom, expectedKind);
        var payload = atom.CanonicalBytes[sizeof(byte)..];
        using var stream = MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false, true)
            : new MemoryStream(payload.ToArray(), 0, payload.Length, false, true);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        try
        {
            object value = expectedKind switch
            {
                SemanticAtomKind.Term => SemanticValueBinaryCodec.Term(reader),
                SemanticAtomKind.Value => SemanticValueBinaryCodec.Read(reader),
                SemanticAtomKind.Identity => SemanticValueBinaryCodec.Identity(reader),
                _ => throw new InvalidDataException("Invalid native Semantic atom kind.")
            };
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Trailing native Semantic atom bytes.");
            }

            return (T)value;
        }
        catch (Exception e) when (e is EndOfStreamException or ArgumentException or FormatException)
        {
            throw new InvalidDataException("Invalid native Semantic atom.", e);
        }
    }

    private static void Validate(SemanticAtomRecord atom, SemanticAtomKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(atom);
        if (atom.Kind != expectedKind || atom.CanonicalBytes.Length < sizeof(byte) ||
            atom.CanonicalBytes.Span[0] != FormatVersion)
        {
            throw new InvalidDataException("Semantic atom kind or format is invalid.");
        }
    }

}
