using System.Globalization;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Core;

/// <summary>Provides a stable ordinal ordering of semantic term identifiers and roles.</summary>
public sealed class SemanticTermOrder : IComparer<SemanticTerm>
{
    /// <summary>Gets the shared semantic term comparer.</summary>
    public static SemanticTermOrder Instance { get; } = new();

    /// <summary>Compares absolute term identifiers ordinally and then roles; null sorts first.</summary>
    public int Compare(SemanticTerm? x, SemanticTerm? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int identifier = StringComparer.Ordinal.Compare(x.Identifier.AbsoluteUri, y.Identifier.AbsoluteUri);
        return identifier != 0 ? identifier : x.Role.CompareTo(y.Role);
    }
}

/// <summary>Orders semantic identities by concept, identity scheme and typed value.</summary>
public sealed class SemanticEntityIdentityOrder : IComparer<SemanticEntityIdentity>
{
    /// <summary>Gets the shared semantic identity comparer.</summary>
    public static SemanticEntityIdentityOrder Instance { get; } = new();

    /// <summary>Compares concept, ordinal scheme identifier and semantic value in sequence; null sorts first.</summary>
    public int Compare(SemanticEntityIdentity? x, SemanticEntityIdentity? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int concept = SemanticTermOrder.Instance.Compare(x.Concept, y.Concept);
        if (concept != 0)
        {
            return concept;
        }

        int scheme = StringComparer.Ordinal.Compare(x.Scheme.Identifier.AbsoluteUri, y.Scheme.Identifier.AbsoluteUri);
        if (scheme != 0)
        {
            return scheme;
        }

        return SemanticValueOrder.Instance.Compare(x.Value, y.Value);
    }

}

/// <summary>Provides a stable kind-then-lexical ordering for semantic access paths; it is not numeric query comparison.</summary>
public sealed class SemanticValueOrder : IComparer<SemanticValue>
{
    /// <summary>Gets the shared semantic access-path value comparer.</summary>
    public static SemanticValueOrder Instance { get; } = new();

    /// <summary>Compares value kinds and invariant lexical representations ordinally; null sorts first.</summary>
    public int Compare(SemanticValue? x, SemanticValue? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int kind = x.Kind.CompareTo(y.Kind);
        return kind != 0 ? kind : StringComparer.Ordinal.Compare(Lexical(x), Lexical(y));
    }

    private static string Lexical(SemanticValue value) => value switch
    {
        NullSemanticValue => string.Empty,
        BooleanSemanticValue item => item.Value ? "true" : "false",
        IntegerSemanticValue item => item.Value.ToString(CultureInfo.InvariantCulture),
        FloatingPointSemanticValue item => item.Value.ToString("R", CultureInfo.InvariantCulture),
        DecimalSemanticValue item => item.Value.ToString(CultureInfo.InvariantCulture),
        StringSemanticValue item => item.Value,
        LanguageStringSemanticValue item => $"{item.Language}\0{item.Value}",
        DateSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        TimeSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        DateTimeSemanticValue item => item.Value.ToString("O", CultureInfo.InvariantCulture),
        DurationSemanticValue item => item.Value.ToString("c", CultureInfo.InvariantCulture),
        BinarySemanticValue item => Convert.ToBase64String(item.Value.Span),
        AbsoluteUriSemanticValue item => item.Value.AbsoluteUri,
        GuidSemanticValue item => item.Value.ToString("D", CultureInfo.InvariantCulture),
        JsonSemanticValue item => item.Utf8Json,
        TypedLiteralSemanticValue item => $"{item.Datatype.AbsoluteUri}\0{item.LexicalValue}",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.Kind, "Unsupported semantic value kind.")
    };
}
