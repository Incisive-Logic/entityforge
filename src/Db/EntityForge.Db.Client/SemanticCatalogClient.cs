using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Semantics;

namespace EntityForge.Db.Client;

/// <summary>A transport-independent, resolved view of the logical Semantic catalogue.</summary>
/// <param name="VocabularyVersion">Published catalog version used to interpret these definitions.</param>
/// <param name="Vocabularies">Published vocabulary identities, versions and term counts.</param>
/// <param name="IdentitySchemes">Available identifier schemes and their value kinds.</param>
/// <param name="Concepts">Resolved concepts with their fields, relationships and hierarchy.</param>
public sealed record SemanticCatalog(
    long VocabularyVersion,
    IReadOnlyList<SemanticCatalogVocabulary> Vocabularies,
    IReadOnlyList<SemanticCatalogIdentityScheme> IdentitySchemes,
    IReadOnlyList<SemanticCatalogConcept> Concepts);

/// <summary>Identifies a published vocabulary and the number of terms it contributes.</summary>
/// <param name="Identifier">Absolute vocabulary identifier.</param>
/// <param name="Version">Publisher-assigned vocabulary version.</param>
/// <param name="ConceptCount">Number of concept terms.</param>
/// <param name="PropertyCount">Number of scalar property terms.</param>
/// <param name="RelationshipCount">Number of relationship terms.</param>
public sealed record SemanticCatalogVocabulary(Uri Identifier, string Version,
    int ConceptCount, int PropertyCount, int RelationshipCount);

/// <summary>Describes the values accepted by a named identity scheme.</summary>
/// <param name="Identifier">Absolute scheme identifier.</param>
/// <param name="DisplayName">Human-readable scheme name.</param>
/// <param name="ValueKind">Semantic kind of identifier values.</param>
/// <param name="Description">Optional explanatory text.</param>
public sealed record SemanticCatalogIdentityScheme(Uri Identifier, string DisplayName,
    SemanticValueKind ValueKind, string? Description = null);

/// <summary>Describes a resolved concept and the catalog relationships used to navigate its schema.</summary>
/// <param name="Identifier">Absolute concept identifier.</param>
/// <param name="DisplayName">Human-readable concept name.</param>
/// <param name="Description">Optional explanatory text.</param>
/// <param name="Properties">Applicable scalar properties and their cardinality constraints.</param>
/// <param name="Relationships">Applicable relationship definitions.</param>
/// <param name="BroaderConcepts">Identifiers of broader concepts in the catalog.</param>
/// <param name="NarrowerConcepts">Identifiers of narrower concepts in the catalog.</param>
/// <param name="EquivalentConcepts">Identifiers declared equivalent to this concept.</param>
public sealed record SemanticCatalogConcept(Uri Identifier, string DisplayName, string? Description,
    IReadOnlyList<SemanticCatalogProperty> Properties,
    IReadOnlyList<SemanticCatalogRelationship> Relationships,
    IReadOnlyList<Uri> BroaderConcepts,
    IReadOnlyList<Uri> NarrowerConcepts,
    IReadOnlyList<Uri> EquivalentConcepts);

/// <summary>Describes a scalar field and its allowed value multiplicity.</summary>
/// <param name="Identifier">Absolute property identifier.</param>
/// <param name="DisplayName">Human-readable property name.</param>
/// <param name="ValueKind">Semantic kind accepted by the property.</param>
/// <param name="Minimum">Minimum number of values.</param>
/// <param name="Maximum">Maximum number of values, or null for no declared upper bound.</param>
/// <param name="Description">Optional explanatory text.</param>
public sealed record SemanticCatalogProperty(Uri Identifier, string DisplayName, SemanticValueKind ValueKind,
    int Minimum, int? Maximum, string? Description = null);

/// <summary>Describes a relationship, its target concepts and cardinality.</summary>
/// <param name="Identifier">Absolute relationship identifier.</param>
/// <param name="DisplayName">Human-readable relationship name.</param>
/// <param name="TargetConcepts">Allowed target-concept identifiers.</param>
/// <param name="Minimum">Minimum number of related entities.</param>
/// <param name="Maximum">Maximum number of related entities, or null for no declared upper bound.</param>
/// <param name="Inverse">Identifier of the inverse relationship, when declared.</param>
/// <param name="Description">Optional explanatory text.</param>
public sealed record SemanticCatalogRelationship(Uri Identifier, string DisplayName,
    IReadOnlyList<Uri> TargetConcepts, int Minimum, int? Maximum, Uri? Inverse,
    string? Description = null);

/// <summary>Reads the resolved logical catalog used to discover Semantic concepts and fields.</summary>
public interface ISemanticCatalogProvider
{
    /// <summary>Retrieves the currently published Semantic catalog.</summary>
    /// <param name="cancellationToken">Cancels the catalog request.</param>
    /// <returns>The resolved vocabulary, identity-scheme and concept definitions.</returns>
    ValueTask<SemanticCatalog> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads and decodes the resolved logical Semantic catalogue from an EntityForgeDB host.</summary>
/// <param name="client">Caller-owned HTTP client configured with the host base address and credentials.</param>
public sealed class HttpSemanticCatalogProvider(HttpClient client) : ISemanticCatalogProvider
{
    /// <inheritdoc />
    /// <exception cref="EntityForgeRemoteException">The host rejects the catalog request.</exception>
    /// <exception cref="InvalidDataException">The response is empty or contains an invalid URI or unknown semantic value kind.</exception>
    public async ValueTask<SemanticCatalog> GetAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await client.GetAsync("v1/semantic/catalog", cancellationToken)
            .ConfigureAwait(false);
        await EntityForgeRemoteErrors.ThrowIfFailureAsync(response, cancellationToken).ConfigureAwait(false);
        return Map(await response.Content.ReadFromJsonAsync(
            SemanticCatalogClientJsonContext.Default.SemanticCatalogTransport, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("EntityForgeDB returned no Semantic catalogue."));
    }

    private static SemanticCatalog Map(SemanticCatalogTransport transport) => new(
        transport.VocabularyVersion,
        transport.Vocabularies.Select(item => new SemanticCatalogVocabulary(Uri(item.Identifier), item.Version,
            item.ConceptCount, item.PropertyCount, item.RelationshipCount)).ToArray(),
        transport.IdentitySchemes.Select(item => new SemanticCatalogIdentityScheme(Uri(item.Identifier),
            item.DisplayName, ValueKind(item.ValueKind), item.Description)).ToArray(),
        transport.Concepts.Select(item => new SemanticCatalogConcept(Uri(item.Identifier), item.DisplayName,
            item.Description,
            item.Properties.Select(property => new SemanticCatalogProperty(Uri(property.Identifier),
                property.DisplayName, ValueKind(property.ValueKind), property.Minimum, property.Maximum,
                property.Description)).ToArray(),
            item.Relationships.Select(relationship => new SemanticCatalogRelationship(Uri(relationship.Identifier),
                relationship.DisplayName, relationship.TargetConcepts.Select(Uri).ToArray(), relationship.Minimum,
                relationship.Maximum, relationship.Inverse is null ? null : Uri(relationship.Inverse),
                relationship.Description)).ToArray(),
            (item.BroaderConcepts ?? []).Select(Uri).ToArray(),
            (item.NarrowerConcepts ?? []).Select(Uri).ToArray(),
            (item.EquivalentConcepts ?? []).Select(Uri).ToArray())).ToArray());

    private static Uri Uri(string value) => System.Uri.TryCreate(value, UriKind.Absolute, out Uri? result)
        ? result : throw new InvalidDataException($"The Semantic catalogue contains invalid URI '{value}'.");

    private static SemanticValueKind ValueKind(string value) => value switch
    {
        "null" => SemanticValueKind.Null,
        "boolean" => SemanticValueKind.Boolean,
        "integer" => SemanticValueKind.Integer,
        "floating-point" => SemanticValueKind.FloatingPoint,
        "decimal" => SemanticValueKind.Decimal,
        "string" => SemanticValueKind.String,
        "language-string" => SemanticValueKind.LanguageString,
        "date" => SemanticValueKind.Date,
        "time" => SemanticValueKind.Time,
        "date-time" => SemanticValueKind.DateTime,
        "duration" => SemanticValueKind.Duration,
        "binary" => SemanticValueKind.Binary,
        "absolute-uri" => SemanticValueKind.AbsoluteUri,
        "guid" => SemanticValueKind.Guid,
        "json" => SemanticValueKind.Json,
        "typed-literal" => SemanticValueKind.TypedLiteral,
        _ => throw new InvalidDataException($"The Semantic catalogue contains unknown value kind '{value}'."),
    };
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SemanticCatalogTransport))]
internal sealed partial class SemanticCatalogClientJsonContext : JsonSerializerContext;
