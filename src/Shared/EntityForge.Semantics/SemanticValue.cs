using System.Globalization;
using EntityForge.Values;
using System.Text.Json.Serialization;

namespace EntityForge.Semantics;

/// <summary>Identifies the representation carried by a semantic value.</summary>
public enum SemanticValueKind
{
    /// <summary>An explicit null value.</summary>
    Null,

    /// <summary>A Boolean value.</summary>
    Boolean,

    /// <summary>A signed 64-bit integer.</summary>
    Integer,

    /// <summary>A double-precision floating-point value.</summary>
    FloatingPoint,

    /// <summary>A .NET decimal value.</summary>
    Decimal,

    /// <summary>A character string without language metadata.</summary>
    String,

    /// <summary>A character string with a runtime-recognized language tag.</summary>
    LanguageString,

    /// <summary>A calendar date without time or zone.</summary>
    Date,

    /// <summary>A time of day without date or zone.</summary>
    Time,

    /// <summary>A date and time with a UTC offset.</summary>
    DateTime,

    /// <summary>A fixed elapsed duration represented by TimeSpan.</summary>
    Duration,

    /// <summary>A byte sequence.</summary>
    Binary,

    /// <summary>An absolute URI.</summary>
    AbsoluteUri,

    /// <summary>A GUID value.</summary>
    Guid,

    /// <summary>A canonical JSON object or array.</summary>
    Json,

    /// <summary>A lexical string with an absolute datatype URI.</summary>
    TypedLiteral
}

/// <summary>Base typed semantic value, serialized using the declared JSON value discriminator.</summary>
/// <param name="Kind">The concrete value representation.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$value")]
[JsonDerivedType(typeof(NullSemanticValue), "null")]
[JsonDerivedType(typeof(BooleanSemanticValue), "boolean")]
[JsonDerivedType(typeof(IntegerSemanticValue), "integer")]
[JsonDerivedType(typeof(FloatingPointSemanticValue), "floatingPoint")]
[JsonDerivedType(typeof(DecimalSemanticValue), "decimal")]
[JsonDerivedType(typeof(StringSemanticValue), "string")]
[JsonDerivedType(typeof(LanguageStringSemanticValue), "languageString")]
[JsonDerivedType(typeof(DateSemanticValue), "date")]
[JsonDerivedType(typeof(TimeSemanticValue), "time")]
[JsonDerivedType(typeof(DateTimeSemanticValue), "dateTime")]
[JsonDerivedType(typeof(DurationSemanticValue), "duration")]
[JsonDerivedType(typeof(BinarySemanticValue), "binary")]
[JsonDerivedType(typeof(AbsoluteUriSemanticValue), "absoluteUri")]
[JsonDerivedType(typeof(GuidSemanticValue), "guid")]
[JsonDerivedType(typeof(JsonSemanticValue), "json")]
[JsonDerivedType(typeof(TypedLiteralSemanticValue), "typedLiteral")]
public abstract record SemanticValue(SemanticValueKind Kind);

/// <summary>An explicit semantic null, distinct from omitting an assertion.</summary>
public sealed record NullSemanticValue() : SemanticValue(SemanticValueKind.Null);

/// <summary>A semantic Boolean value.</summary>
/// <param name="Value">The Boolean value.</param>
public sealed record BooleanSemanticValue(bool Value) : SemanticValue(SemanticValueKind.Boolean);

/// <summary>A semantic signed 64-bit integer, rather than an arbitrary-precision integer.</summary>
/// <param name="Value">The signed integer value.</param>
public sealed record IntegerSemanticValue(long Value) : SemanticValue(SemanticValueKind.Integer);

/// <summary>A semantic double-precision floating-point value.</summary>
/// <param name="Value">The floating-point value; this constructor does not reject nonfinite values.</param>
public sealed record FloatingPointSemanticValue(double Value) : SemanticValue(SemanticValueKind.FloatingPoint);

/// <summary>A semantic decimal within the .NET decimal range and precision.</summary>
/// <param name="Value">The decimal value.</param>
public sealed record DecimalSemanticValue(decimal Value) : SemanticValue(SemanticValueKind.Decimal);

/// <summary>A nonnull semantic string without language metadata.</summary>
public sealed record StringSemanticValue : SemanticValue
{
    /// <summary>Captures the exact character string without normalization.</summary>
    /// <param name="value">The string, which may be empty.</param>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    public StringSemanticValue(string value) : base(SemanticValueKind.String)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the exact captured string.</summary>
    public string Value
    {
        get;
    }
}

/// <summary>A semantic string with a language tag validated and normalized by the runtime's CultureInfo implementation.</summary>
/// <remarks>Accepted tags depend on runtime globalization data; this is not the RDF LANGTAG lexical validator.</remarks>
public sealed record LanguageStringSemanticValue : SemanticValue
{
    /// <summary>Captures a string and resolves the supplied language through CultureInfo.</summary>
    /// <param name="value">The nonnull string, which may be empty.</param>
    /// <param name="language">The nonblank language name recognized by the runtime.</param>
    /// <exception cref="ArgumentException">An argument is null, the language is blank or the runtime does not recognize it.</exception>
    public LanguageStringSemanticValue(string value, string language) : base(SemanticValueKind.LanguageString)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        try
        {
            Language = CultureInfo.GetCultureInfo(language).Name;
        }
        catch (CultureNotFoundException exception)
        {
            throw new ArgumentException("A language string requires a valid BCP 47 language tag.", nameof(language), exception);
        }
    }

    /// <summary>Gets the exact captured string.</summary>
    public string Value
    {
        get;
    }

    /// <summary>Gets the language name returned by CultureInfo, which may differ in spelling or case from the input.</summary>
    public string Language
    {
        get;
    }
}

/// <summary>A semantic calendar date without a time or time zone.</summary>
/// <param name="Value">The date.</param>
public sealed record DateSemanticValue(DateOnly Value) : SemanticValue(SemanticValueKind.Date);

/// <summary>A semantic time of day without a date or time zone.</summary>
/// <param name="Value">The time of day at TimeOnly precision.</param>
public sealed record TimeSemanticValue(TimeOnly Value) : SemanticValue(SemanticValueKind.Time);

/// <summary>A semantic date and time with an explicit offset in its in-memory representation.</summary>
/// <param name="Value">The date, time and UTC offset; storage adapters may impose additional normalization rules.</param>
public sealed record DateTimeSemanticValue(DateTimeOffset Value) : SemanticValue(SemanticValueKind.DateTime);

/// <summary>A semantic fixed duration, without calendar-dependent month or year components.</summary>
/// <param name="Value">The signed elapsed duration.</param>
public sealed record DurationSemanticValue(TimeSpan Value) : SemanticValue(SemanticValueKind.Duration);

/// <summary>A semantic byte sequence captured independently of its input buffer.</summary>
public sealed record BinarySemanticValue : SemanticValue
{
    /// <summary>Copies the supplied bytes into an owned array.</summary>
    /// <param name="value">The bytes to copy; an empty sequence is valid.</param>
    public BinarySemanticValue(ReadOnlyMemory<byte> value) : base(SemanticValueKind.Binary)
    {
        Value = value.ToArray();
    }

    /// <summary>Gets a read-only memory view of the captured bytes.</summary>
    public ReadOnlyMemory<byte> Value
    {
        get;
    }
}

/// <summary>A semantic absolute URI value.</summary>
public sealed record AbsoluteUriSemanticValue : SemanticValue
{
    /// <summary>Retains an absolute URI without fetching its resource.</summary>
    /// <param name="value">The nonnull absolute URI.</param>
    /// <exception cref="ArgumentException">The URI is null or relative.</exception>
    public AbsoluteUriSemanticValue(Uri value) : base(SemanticValueKind.AbsoluteUri)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value.IsAbsoluteUri
            ? value
            : throw new ArgumentException("A semantic URI value must be absolute.", nameof(value));
    }

    /// <summary>Gets the retained absolute URI.</summary>
    public Uri Value
    {
        get;
    }
}

/// <summary>A semantic GUID value.</summary>
/// <param name="Value">The GUID; this representation permits the empty GUID.</param>
public sealed record GuidSemanticValue(Guid Value) : SemanticValue(SemanticValueKind.Guid);

/// <summary>A structured semantic JSON value with an object or array root.</summary>
public sealed record JsonSemanticValue : SemanticValue
{
    /// <summary>Parses and canonicalizes a structured JSON document.</summary>
    /// <param name="utf8Json">JSON text; despite the parameter name this is a .NET string, not encoded bytes.</param>
    /// <exception cref="ArgumentException">The parsed root is neither an object nor an array.</exception>
    public JsonSemanticValue(string utf8Json) : base(SemanticValueKind.Json)
    {
        CanonicalValue = CanonicalJsonValue.Parse(utf8Json);
        if (!CanonicalValue.IsStructured)
        {
            throw new ArgumentException("A semantic JSON bag must have an object or array root.", nameof(utf8Json));
        }

        Utf8Json = CanonicalValue.Value;
    }

    /// <summary>Gets canonical JSON text as a .NET string; callers choose an encoding when writing bytes.</summary>
    public string Utf8Json
    {
        get;
    }

    /// <summary>Gets the parsed canonical JSON representation.</summary>
    [JsonIgnore]
    public CanonicalJsonValue CanonicalValue
    {
        get;
    }
}

/// <summary>A lexical semantic literal retaining its datatype URI without validating that datatype's lexical space.</summary>
public sealed record TypedLiteralSemanticValue : SemanticValue
{
    /// <summary>Captures a literal's lexical spelling and absolute datatype URI.</summary>
    /// <param name="lexicalValue">The nonnull lexical spelling, which may be empty.</param>
    /// <param name="datatype">The nonnull absolute datatype URI.</param>
    /// <exception cref="ArgumentException">An argument is null or the datatype URI is relative.</exception>
    public TypedLiteralSemanticValue(string lexicalValue, Uri datatype) : base(SemanticValueKind.TypedLiteral)
    {
        LexicalValue = lexicalValue ?? throw new ArgumentNullException(nameof(lexicalValue));
        ArgumentNullException.ThrowIfNull(datatype);
        Datatype = datatype.IsAbsoluteUri
            ? datatype
            : throw new ArgumentException("A typed literal datatype must be an absolute URI.", nameof(datatype));
    }

    /// <summary>Gets the original lexical spelling.</summary>
    public string LexicalValue
    {
        get;
    }

    /// <summary>Gets the absolute datatype URI.</summary>
    public Uri Datatype
    {
        get;
    }
}
