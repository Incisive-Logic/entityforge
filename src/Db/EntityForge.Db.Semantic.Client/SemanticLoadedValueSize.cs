using System.Text;
using EntityForge.Query.Values;

namespace EntityForge.Db.Semantic;

// Loaded scalars use a binary-size budget: tag, fixed-size scalars and length-prefixed
// UTF-8 fields. Count directly, without constructing a second serialized value.
internal static class SemanticLoadedValueSize
{
    private const int TagBytes = 1;
    private const int GuidBytes = 16;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static long Text(string? value) => sizeof(int) + (value is null ? 0L : Utf8.GetByteCount(value));

    internal static long Count(QueryValue value) => checked(TagBytes + (value switch
    {
        NullValue or UnboundValue => 0L,
        IdentifierValue item => Text(item.Identifier),
        BlankNodeValue item => Text(item.Identifier) + GuidBytes,
        LiteralValue item => Text(item.LexicalValue) + sizeof(int) + Text(item.DatatypeIdentifier?.AbsoluteUri) +
            Text(item.Language) + sizeof(int),
        JsonValue item => sizeof(int) + (long)item.Value.Utf8ByteCount,
        _ => throw new InvalidDataException("A mapped scalar projection contains a non-scalar value.")
    }));
}
