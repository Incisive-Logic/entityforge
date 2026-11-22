namespace EntityForge.Semantics;

/// <summary>Maps typed Semantic values to their RDF-compatible datatype identifiers.</summary>
public static class SemanticValueDatatypes
{
    private const string XmlSchema = "http://www.w3.org/2001/XMLSchema#";
    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private const string Csvw = "http://www.w3.org/ns/csvw#";
    private const string EntityForge = "https://schemas.entityforge.org/datatypes/";

    /// <summary>Returns the datatype identifier associated with a Semantic value kind.</summary>
    /// <param name="value">The typed value whose datatype is requested.</param>
    /// <returns>The datatype URI, a typed literal's explicit datatype, or null for a Semantic null value.</returns>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The value implementation is not supported.</exception>
    public static Uri? GetDatatype(SemanticValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            NullSemanticValue => null,
            BooleanSemanticValue => Uri(XmlSchema + "boolean"),
            IntegerSemanticValue => Uri(XmlSchema + "integer"),
            FloatingPointSemanticValue => Uri(XmlSchema + "double"),
            DecimalSemanticValue => Uri(XmlSchema + "decimal"),
            StringSemanticValue => Uri(XmlSchema + "string"),
            LanguageStringSemanticValue => Uri(Rdf + "langString"),
            DateSemanticValue => Uri(XmlSchema + "date"),
            TimeSemanticValue => Uri(XmlSchema + "time"),
            DateTimeSemanticValue => Uri(XmlSchema + "dateTime"),
            DurationSemanticValue => Uri(XmlSchema + "duration"),
            BinarySemanticValue => Uri(XmlSchema + "base64Binary"),
            AbsoluteUriSemanticValue => Uri(XmlSchema + "anyURI"),
            GuidSemanticValue => Uri(EntityForge + "guid"),
            JsonSemanticValue => Uri(Csvw + "JSON"),
            TypedLiteralSemanticValue literal => literal.Datatype,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    private static Uri Uri(string value) => new(value, UriKind.Absolute);
}
