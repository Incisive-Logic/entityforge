using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Application-owned display and validation hints for one semantic field.</summary>
/// <param name="Term">Semantic property URI.</param>
/// <param name="ProjectionName">Field name in the projected document.</param>
/// <param name="Label">Application-owned display label.</param>
/// <param name="ScalarKind">Optional expected scalar kind used as a presentation hint.</param>
/// <param name="Multiple">Whether the presentation permits multiple values.</param>
/// <param name="Required">Whether the presentation marks the field as required; this does not install database validation.</param>
/// <param name="Group">Optional presentation grouping label.</param>
/// <param name="Order">Display ordering value.</param>
/// <param name="Format">Optional application-interpreted format hint.</param>
public sealed record SemanticFieldPresentation(Uri Term, string ProjectionName, string Label,
    QueryScalarKind? ScalarKind = null, bool Multiple = false, bool Required = false,
    string? Group = null, int Order = 0, string? Format = null);

/// <summary>Application-owned display hints for one projected semantic relationship.</summary>
/// <param name="Term">Semantic relationship URI.</param>
/// <param name="ProjectionName">Relationship name in the projected document.</param>
/// <param name="Label">Application-owned display label.</param>
/// <param name="Multiple">Whether the presentation permits multiple related entities.</param>
/// <param name="Group">Optional presentation grouping label.</param>
/// <param name="Order">Display ordering value.</param>
public sealed record SemanticRelationshipPresentation(Uri Term, string ProjectionName, string Label,
    bool Multiple = true, string? Group = null, int Order = 0);

/// <summary>Framework-neutral presentation metadata registered by an application for one concept.</summary>
/// <param name="Concept">Concept URI used to select this presentation.</param>
/// <param name="Label">Application-owned concept label.</param>
/// <param name="Fields">Presentation hints for projected scalar fields.</param>
/// <param name="Relationships">Presentation hints for projected relationships.</param>
public sealed record SemanticConceptPresentation(Uri Concept, string Label,
    IReadOnlyList<SemanticFieldPresentation> Fields,
    IReadOnlyList<SemanticRelationshipPresentation> Relationships);

/// <summary>Immutable registry of presentation vocabularies; it contains no UI-framework types.</summary>
public sealed class SemanticPresentationRegistry
{
    private readonly IReadOnlyDictionary<Uri, SemanticConceptPresentation> _concepts;

    /// <summary>Copies concept presentations into a registry keyed by concept URI.</summary>
    /// <param name="concepts">Presentations with unique concept identifiers.</param>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <exception cref="ArgumentException">Multiple presentations have the same concept identifier.</exception>
    public SemanticPresentationRegistry(IEnumerable<SemanticConceptPresentation> concepts)
    {
        ArgumentNullException.ThrowIfNull(concepts);
        _concepts = concepts.ToDictionary(item => item.Concept);
    }

    /// <summary>Looks up application-owned presentation metadata for a concept identifier.</summary>
    /// <param name="concept">The concept URI to resolve.</param>
    /// <param name="presentation">Receives the registered presentation when found.</param>
    /// <returns>True when the registry contains the concept.</returns>
    public bool TryGet(Uri concept, out SemanticConceptPresentation presentation) =>
        _concepts.TryGetValue(concept, out presentation!);
}

/// <summary>One projected field plus its semantic, cardinality, validation, evidence and presentation metadata.</summary>
/// <param name="ProjectionName">Field name from the projected document.</param>
/// <param name="SemanticTerm">Semantic property URI.</param>
/// <param name="Label">Resolved display label.</param>
/// <param name="ScalarKind">Optional scalar kind hint.</param>
/// <param name="Multiple">Resolved multiple-value presentation flag.</param>
/// <param name="Required">Resolved required-field presentation hint; not a database constraint.</param>
/// <param name="Group">Optional presentation grouping label.</param>
/// <param name="Order">Resolved display ordering value.</param>
/// <param name="Format">Optional application-interpreted format hint.</param>
/// <param name="Values">Projected values with their evidence; no values are synthesized by presentation hints.</param>
public sealed record SemanticDynamicFieldDescriptor(string ProjectionName, Uri SemanticTerm, string Label,
    QueryScalarKind? ScalarKind, bool Multiple, bool Required, string? Group, int Order, string? Format,
    IReadOnlyList<EntityProjectionValue> Values)
{
    /// <summary>Gets whether any projected value carries at least one evidence reference.</summary>
    public bool HasEvidence => Values.Any(value => value.Evidence.Count != 0);
}

/// <summary>One projected relationship plus its optional semantic and application presentation metadata.</summary>
/// <param name="ProjectionName">Relationship name from the projected document.</param>
/// <param name="SemanticTerm">Semantic relationship URI when available.</param>
/// <param name="Label">Resolved display label.</param>
/// <param name="Multiple">Resolved multiple-entity presentation flag.</param>
/// <param name="Group">Optional presentation grouping label.</param>
/// <param name="Order">Resolved display ordering value.</param>
/// <param name="Items">Related projection items retained from the document.</param>
public sealed record SemanticDynamicRelationshipDescriptor(string ProjectionName, Uri? SemanticTerm, string Label,
    bool Multiple, string? Group, int Order, IReadOnlyList<EntityProjectionItem> Items);

/// <summary>A framework-neutral dynamic entity suitable for generic application ViewModels or templates.</summary>
/// <param name="Identity">Identity of the projected entity.</param>
/// <param name="Concept">Selected concept URI when available.</param>
/// <param name="Label">Resolved entity display label.</param>
/// <param name="Fields">Fields in resolved presentation order.</param>
/// <param name="Relationships">Relationships in resolved presentation order.</param>
/// <param name="Document">Original neutral entity projection, including its evidence.</param>
public sealed record SemanticDynamicEntityDescriptor(GraphElementIdentity Identity, Uri? Concept, string Label,
    IReadOnlyList<SemanticDynamicFieldDescriptor> Fields,
    IReadOnlyList<SemanticDynamicRelationshipDescriptor> Relationships,
    EntityProjectionNode Document);

/// <summary>Combines a neutral evidence-bearing document with optional application-owned presentation metadata.</summary>
public static class SemanticDynamicEntityComposer
{
    /// <summary>Combines an entity document with optional presentation hints without changing its values or evidence.</summary>
    /// <param name="document">The projected entity to describe.</param>
    /// <param name="registry">Application-owned concept presentation definitions.</param>
    /// <returns>A descriptor ordered by presentation order and projection name; missing hints use values inferred from the document.</returns>
    /// <exception cref="ArgumentNullException">The document or registry is null.</exception>
    public static SemanticDynamicEntityDescriptor Compose(EntityProjectionNode document,
        SemanticPresentationRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(registry);
        Uri? concept = document.Types.FirstOrDefault(item => item.Role is SemanticTermRole.Concept)?.Identifier;
        SemanticConceptPresentation? presentation = null;
        if (concept is not null)
        {
            registry.TryGet(concept, out presentation!);
        }

        IReadOnlyList<SemanticDynamicFieldDescriptor> fields = document.Fields.Select(field =>
        {
            SemanticFieldPresentation? hint = presentation?.Fields.FirstOrDefault(item =>
                String.Equals(item.ProjectionName, field.Name, StringComparison.Ordinal));
            Uri term = hint?.Term ?? field.Values.FirstOrDefault()?.ProjectedTerm.Identifier ??
                new Uri($"urn:entityforge:projection:{Uri.EscapeDataString(field.Name)}");
            QueryScalarKind? kind = hint?.ScalarKind ?? InferKind(field.Values);
            return new SemanticDynamicFieldDescriptor(field.Name, term, hint?.Label ?? field.Name, kind,
                hint?.Multiple ?? field.Values.Count > 1, hint?.Required ?? false, hint?.Group, hint?.Order ?? 0,
                hint?.Format, field.Values);
        }).OrderBy(item => item.Order).ThenBy(item => item.ProjectionName, StringComparer.Ordinal).ToArray();

        IReadOnlyList<SemanticDynamicRelationshipDescriptor> relationships = document.Relationships.Select(item =>
        {
            SemanticRelationshipPresentation? hint = presentation?.Relationships.FirstOrDefault(candidate =>
                String.Equals(candidate.ProjectionName, item.Name, StringComparison.Ordinal));
            return new SemanticDynamicRelationshipDescriptor(item.Name, hint?.Term, hint?.Label ?? item.Name,
                hint?.Multiple ?? true, hint?.Group, hint?.Order ?? 0, item.Items);
        }).OrderBy(item => item.Order).ThenBy(item => item.ProjectionName, StringComparer.Ordinal).ToArray();

        return new(document.Identity, concept, presentation?.Label ?? concept?.AbsoluteUri ?? "Entity",
            fields, relationships, document);
    }

    private static QueryScalarKind? InferKind(IReadOnlyList<EntityProjectionValue> values)
    {
        QueryScalarKind[] kinds = values.Select(item => item.Value).OfType<LiteralValue>()
            .Select(item => item.ScalarKind).Distinct().ToArray();
        return kinds.Length == 1 ? kinds[0] : null;
    }
}
