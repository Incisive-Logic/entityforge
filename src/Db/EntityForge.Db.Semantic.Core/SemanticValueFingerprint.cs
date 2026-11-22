using System.Globalization;
using System.Security.Cryptography;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Core;

internal static class SemanticValueFingerprint
{
    public static string Create(SemanticValue value)
    {
        byte[] digest = value switch
        {
            NullSemanticValue => SHA256.HashData(ReadOnlySpan<byte>.Empty),
            BooleanSemanticValue item => SHA256.HashData(new byte[] { item.Value ? (byte)1 : (byte)0 }),
            IntegerSemanticValue item => Text(item.Value.ToString(CultureInfo.InvariantCulture)),
            FloatingPointSemanticValue item => Text(DoubleKey(item.Value)),
            DecimalSemanticValue item => Text(item.Value.ToString("G29", CultureInfo.InvariantCulture)),
            StringSemanticValue item => Text(item.Value),
            LanguageStringSemanticValue item => DatabaseUtf8Hash.HashJoined([item.Language, item.Value], "\n"),
            DateSemanticValue item => Text(item.Value.ToString("O", CultureInfo.InvariantCulture)),
            TimeSemanticValue item => Text(item.Value.ToString("O", CultureInfo.InvariantCulture)),
            DateTimeSemanticValue item => Text(item.Value.UtcTicks.ToString(CultureInfo.InvariantCulture)),
            DurationSemanticValue item => Text(item.Value.Ticks.ToString(CultureInfo.InvariantCulture)),
            BinarySemanticValue item => SHA256.HashData(item.Value.Span),
            AbsoluteUriSemanticValue item => Text(item.Value.AbsoluteUri),
            GuidSemanticValue item => Text(item.Value.ToString("D", CultureInfo.InvariantCulture)),
            JsonSemanticValue item => Text(item.Utf8Json),
            TypedLiteralSemanticValue item => DatabaseUtf8Hash.HashJoined([item.Datatype.AbsoluteUri, item.LexicalValue], "\n"),
            _ => throw new InvalidDataException($"Unsupported semantic statistics value kind '{value.Kind}'.")
        };
        return $"{(int)value.Kind}:{Convert.ToHexString(digest)}";
    }

    private static byte[] Text(string value) => DatabaseUtf8Hash.Hash(value);

    private static string DoubleKey(double value) => Double.IsNaN(value) ? "NaN"
        : value == 0D ? "0" : value.ToString("R", CultureInfo.InvariantCulture);
}
