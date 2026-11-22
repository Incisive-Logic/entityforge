using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal static class SemanticEntityPlacement
{
    internal const string SourcePrefix = "urn:entityforge:aggregate:root:v3:";

    internal static string OwnerKey(SemanticEntityIdentity identity) => Digest(CanonicalKey(identity));

    internal static Uri Partition(SemanticEntityIdentity identity) => new(SourcePrefix + OwnerKey(identity));

    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string CanonicalKey(SemanticEntityIdentity identity) =>
        Field(identity.Concept.Identifier.AbsoluteUri) + Field(SemanticUri(identity.Scheme.Identifier)) +
        Field(((int)identity.Value.Kind).ToString(CultureInfo.InvariantCulture)) + Field(SemanticValue(identity.Value));

    private static string SemanticValue(SemanticValue value) => value switch
    {
        NullSemanticValue => String.Empty,
        BooleanSemanticValue item => item.Value ? "true" : "false",
        IntegerSemanticValue item => item.Value.ToString(CultureInfo.InvariantCulture),
        FloatingPointSemanticValue item => item.Value == 0 ? "0" : item.Value.ToString("R", CultureInfo.InvariantCulture),
        DecimalSemanticValue item => item.Value == 0 ? "0" : item.Value.ToString("G29", CultureInfo.InvariantCulture),
        StringSemanticValue item => item.Value,
        LanguageStringSemanticValue item => Field(item.Value) + Field(item.Language),
        DateSemanticValue item => item.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        DateTimeSemanticValue item => item.Value.UtcTicks.ToString(CultureInfo.InvariantCulture),
        DurationSemanticValue item => item.Value.Ticks.ToString(CultureInfo.InvariantCulture),
        BinarySemanticValue item => Convert.ToHexString(item.Value.Span),
        AbsoluteUriSemanticValue item => SemanticUri(item.Value),
        GuidSemanticValue item => item.Value.ToString("N"),
        JsonSemanticValue item => item.Utf8Json,
        TypedLiteralSemanticValue item => Field(item.LexicalValue) + Field(SemanticUri(item.Datatype)),
        _ => throw new InvalidDataException($"Unsupported Semantic identity value '{value.GetType().Name}'.")
    };

    internal static string SemanticUri(Uri value)
    {
        var components = UriComponents.HttpRequestUrl;
        if (value.Scheme == Uri.UriSchemeMailto)
        {
            components |= UriComponents.UserInfo;
        }

        string key = value.GetComponents(components, UriFormat.SafeUnescaped);
        if (!value.IsFile)
        {
            return key;
        }

        string folded = key.ToUpperInvariant();
        return Uri.TryCreate(folded, UriKind.Absolute, out Uri? candidate) && value.Equals(candidate)
            ? folded : key;
    }

    private static string Field(string value) => $"{value.Length}:{value}";

}
