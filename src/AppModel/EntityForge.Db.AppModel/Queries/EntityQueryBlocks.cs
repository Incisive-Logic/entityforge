using System.Text.Json;
using EntityForge.Db.Client;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>A concept advertised by the Semantic profile's authoritative catalogue.</summary>
/// <param name="Identifier">Catalog concept identity used in requests.</param>
/// <param name="DisplayName">Human-readable label, not an alternative identity.</param>
/// <param name="Description">Optional catalog description.</param>
public sealed record SemanticQueryConcept(string Identifier, string DisplayName, string? Description = null);

/// <summary>A property defined for a Semantic concept.</summary>
/// <param name="Identifier">Catalog property identity.</param>
/// <param name="DisplayName">Label displayed by the builder.</param>
/// <param name="Editor">Value editor and scalar validation category.</param>
/// <param name="Operators">Comparisons advertised for this property.</param>
/// <param name="Description">Optional catalog description.</param>
/// <param name="LiteralKind">Optional refinement such as integer, guid or absoluteUri.</param>
public sealed record SemanticQueryProperty(string Identifier, string DisplayName, QueryValueEditor Editor,
    IReadOnlySet<QueryComparisonOperator> Operators, string? Description = null, string? LiteralKind = null);

/// <summary>A relationship defined by the Semantic catalogue, including its valid target concepts.</summary>
/// <param name="Identifier">Catalog relationship identity.</param>
/// <param name="DisplayName">Label displayed by the builder.</param>
/// <param name="TargetTypes">Advertised target concept identities, when constrained by the catalog.</param>
/// <param name="Description">Optional catalog description.</param>
public sealed record SemanticQueryRelationship(string Identifier, string DisplayName,
    IReadOnlyList<string>? TargetTypes = null, string? Description = null);

/// <summary>Scalar input categories for the simple query builder, not a complete database type system.</summary>
public enum QueryValueEditor
{
    /// <summary>A JSON string.</summary>
    Text,

    /// <summary>A JSON number representable as decimal, or as Int64 when the literal kind is integer.</summary>
    Number,

    /// <summary>A JSON true or false value.</summary>
    Boolean,

    /// <summary>A date string in yyyy-MM-dd form.</summary>
    Date,

    /// <summary>A date/time string with an explicit UTC marker or numeric offset.</summary>
    DateTime,

    /// <summary>A string identity, optionally constrained by its literal kind.</summary>
    Identifier,

    /// <summary>A string selected through a choice editor.</summary>
    Choice
}

/// <summary>Property comparison operations available to query blocks; each property advertises its supported subset.</summary>
public enum QueryComparisonOperator
{
    /// <summary>Compare for equality.</summary>
    Equals,

    /// <summary>Compare for inequality.</summary>
    NotEquals,

    /// <summary>Test whether text contains the supplied text.</summary>
    Contains,

    /// <summary>Test whether text starts with the supplied text.</summary>
    StartsWith,

    /// <summary>Compare as greater than the supplied value.</summary>
    GreaterThan,

    /// <summary>Compare as greater than or equal to the supplied value.</summary>
    GreaterThanOrEqual,

    /// <summary>Compare as less than the supplied value.</summary>
    LessThan,

    /// <summary>Compare as less than or equal to the supplied value.</summary>
    LessThanOrEqual,

    /// <summary>Test property presence without consuming an operand value.</summary>
    IsPresent,

    /// <summary>Negate the property-presence test without consuming an operand value.</summary>
    IsMissing,

    /// <summary>Match any value from a nonempty JSON array by equality.</summary>
    In
}

/// <summary>Combines conditions within a logical group or one relationship target.</summary>
public enum QueryLogicalOperator
{
    /// <summary>Require every child condition.</summary>
    All,

    /// <summary>Require at least one child condition.</summary>
    Any,

    /// <summary>Negate the disjunction of child conditions.</summary>
    None
}

/// <summary>Direction applied to an ordering key.</summary>
public enum QuerySortDirection
{
    /// <summary>Sort smaller keys first.</summary>
    Ascending,

    /// <summary>Sort larger keys first.</summary>
    Descending
}

/// <summary>Selects an ordering key from a property's multiple values.</summary>
public enum QuerySortValueSelection
{
    /// <summary>Use the minimum property value.</summary>
    Minimum,

    /// <summary>Use the maximum property value.</summary>
    Maximum
}

/// <summary>Placement of entities without an ordering value.</summary>
public enum QueryMissingValuePlacement
{
    /// <summary>Place missing values before present values.</summary>
    First,

    /// <summary>Place missing values after present values.</summary>
    Last
}

/// <summary>Selects direct or entailed concept membership for the primary concept.</summary>
public enum QueryConceptMatchMode
{
    /// <summary>Use entailed membership, including supported narrower-concept inference.</summary>
    IncludeNarrowerConcepts,

    /// <summary>Use direct membership in the requested concept.</summary>
    ExactConceptOnly
}

/// <summary>Catalog-derived definitions used to validate and present builder queries.</summary>
/// <param name="Concepts">Available primary and relationship-target concepts.</param>
/// <param name="PropertiesByConcept">Property definitions indexed by concept identity.</param>
/// <param name="RelationshipsByConcept">Relationship definitions indexed by source concept identity.</param>
public sealed record SemanticQueryMetadata(
    IReadOnlyList<SemanticQueryConcept> Concepts,
    IReadOnlyDictionary<string, IReadOnlyList<SemanticQueryProperty>> PropertiesByConcept,
    IReadOnlyDictionary<string, IReadOnlyList<SemanticQueryRelationship>> RelationshipsByConcept);

/// <summary>Loads semantic query-builder definitions from an authoritative source.</summary>
public interface ISemanticQueryMetadataProvider
{
    /// <summary>Asynchronously obtains the provider's metadata.</summary>
    /// <param name="cancellationToken">Cancellation for metadata retrieval.</param>
    /// <returns>Concept, property and relationship definitions; caching and refresh policy depend on the provider.</returns>
    ValueTask<SemanticQueryMetadata> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Base for serializable builder conditions, discriminated by the JSON $block property.</summary>
/// <param name="Id">Application block identity used by the editor, not a database entity identity.</param>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$block")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SemanticPropertyConditionBlock), "property")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SemanticRelationshipConditionBlock), "relationship")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(SemanticLogicalQueryBlock), "group")]
public abstract record SemanticQueryBlock(string Id);

/// <summary>Applies one comparison to a catalog property.</summary>
/// <param name="Id">Editor block identity.</param>
/// <param name="Property">Property definition, including its value category.</param>
/// <param name="Operator">Requested comparison.</param>
/// <param name="Value">Scalar operand, or nonempty array for In; presence tests ignore it. Keep its owning JSON document alive or supply a cloned element.</param>
public sealed record SemanticPropertyConditionBlock(string Id, SemanticQueryProperty Property,
    QueryComparisonOperator Operator, JsonElement Value) : SemanticQueryBlock(Id);

/// <summary>Tests existence of an outgoing relationship to a matching target, optionally negating that existence.</summary>
/// <param name="Id">Editor block identity.</param>
/// <param name="Relationship">Outgoing relationship definition.</param>
/// <param name="TargetConcept">Required target concept.</param>
/// <param name="Operator">Combines conditions on an individual target; None negates their disjunction, not relationship existence.</param>
/// <param name="Conditions">Conditions evaluated against the target.</param>
/// <param name="Negated">Whether to negate the complete relationship-existence predicate.</param>
public sealed record SemanticRelationshipConditionBlock(string Id, SemanticQueryRelationship Relationship,
    SemanticQueryConcept TargetConcept, QueryLogicalOperator Operator, IReadOnlyList<SemanticQueryBlock> Conditions, bool Negated = false)
    : SemanticQueryBlock(Id);

/// <summary>Groups conditions in their current concept scope.</summary>
/// <param name="Id">Editor block identity.</param>
/// <param name="Operator">Logical combination of the children.</param>
/// <param name="Children">Child conditions, which may contain nested groups or relationships.</param>
public sealed record SemanticLogicalQueryBlock(string Id, QueryLogicalOperator Operator,
    IReadOnlyList<SemanticQueryBlock> Children) : SemanticQueryBlock(Id);

/// <summary>Defines one semantic entity ordering key.</summary>
/// <param name="Property">Property from which to select the key.</param>
/// <param name="Direction">Ordering direction, ascending by default.</param>
/// <param name="ValueSelection">How multiple property values produce one key; minimum by default.</param>
/// <param name="MissingValues">Placement of absent keys, last by default.</param>
public sealed record SemanticQueryOrdering(SemanticQueryProperty Property,
    QuerySortDirection Direction = QuerySortDirection.Ascending,
    QuerySortValueSelection ValueSelection = QuerySortValueSelection.Minimum,
    QueryMissingValuePlacement MissingValues = QueryMissingValuePlacement.Last);

/// <summary>Projects a property under a result-field name.</summary>
/// <param name="Property">Catalog property to project.</param>
/// <param name="ResultName">Requested field name in the projection.</param>
public sealed record SemanticQueryProjectionField(SemanticQueryProperty Property, string ResultName);

/// <summary>
/// User-authored query shape for the Semantic profile. RDF and Property Graph use their own
/// native query models; only their result transport may converge through EntityProjection.
/// </summary>
/// <param name="PrimaryConcept">Concept whose entities form the query's input.</param>
/// <param name="Conditions">Top-level conditions combined conjunctively; an empty list adds no filter.</param>
/// <param name="Projection">Requested property fields and result names.</param>
/// <param name="First">Requested page size, not a promise that the complete result fits in one page.</param>
/// <param name="IncludeEvidence">Whether to request supporting evidence with results.</param>
/// <param name="ConceptMatchMode">Direct or entailed concept matching.</param>
/// <param name="OrderBy">Ordered sort keys, or null for the service's default ordering.</param>
/// <remarks>Construction does not validate catalog membership, limits or value shapes. Use builder support checks before editing and retain server-side validation for execution. This is not a GQL, SPARQL or GraphQL syntax tree.</remarks>
public sealed record SemanticQueryDocument(
    SemanticQueryConcept PrimaryConcept,
    IReadOnlyList<SemanticQueryBlock> Conditions,
    IReadOnlyList<SemanticQueryProjectionField> Projection,
    int First = 50,
    bool IncludeEvidence = true,
    QueryConceptMatchMode ConceptMatchMode = QueryConceptMatchMode.IncludeNarrowerConcepts,
    IReadOnlyList<SemanticQueryOrdering>? OrderBy = null);

/// <summary>Provides fallback labels when catalog display names are unavailable.</summary>
public static class QueryDisplayName
{
    /// <summary>Builds a label from the decoded final slash- or hash-separated identifier component.</summary>
    /// <param name="identifier">Nonblank identifier to label.</param>
    /// <returns>A label with underscores/hyphens replaced by spaces, lower-to-upper transitions separated and its initial character uppercased invariantly.</returns>
    /// <remarks>This is display formatting, not identifier normalization or a reversible transformation.</remarks>
    /// <exception cref="ArgumentException">The identifier is blank.</exception>
    public static string FromIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        string decoded = Uri.UnescapeDataString(identifier.TrimEnd('/', '#'));
        int separator = Math.Max(decoded.LastIndexOf('/'), decoded.LastIndexOf('#'));
        string local = separator >= 0 ? decoded[(separator + 1)..] : decoded;
        if (local.Length == 0)
        {
            return identifier;
        }

        var words = new List<char>(local.Length + 8);
        for (int index = 0; index < local.Length; index++)
        {
            char value = local[index];
            if (index > 0 && Char.IsUpper(value) && Char.IsLower(local[index - 1]))
            {
                words.Add(' ');
            }

            words.Add(value is '_' or '-' ? ' ' : value);
        }
        string result = new(words.ToArray());
        return Char.ToUpperInvariant(result[0]) + result[1..];
    }
}
