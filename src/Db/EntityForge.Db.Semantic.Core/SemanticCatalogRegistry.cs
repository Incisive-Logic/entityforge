using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Core;

/// <summary>Tracks registered models, terms, identity schemes and published vocabularies.</summary>
public sealed class SemanticCatalogRegistry
{
    private const int RequiredUniqueDefinitionCount = 1;
    private readonly IReadOnlyDictionary<string, SemanticTermRole> _termRoles;
    private readonly IReadOnlyDictionary<string, SemanticIdentityScheme> _identitySchemes;

    /// <summary>Restores a registry from the state's catalog, or derives its initial entries from retained evidence when the catalog is empty.</summary>
    public SemanticCatalogRegistry(SemanticDatabaseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Catalog.Models.Count != 0 || state.Catalog.Terms.Count != 0 ||
            state.Catalog.IdentitySchemes.Count != 0 || state.Catalog.Vocabularies.Count != 0)
        {
            Models = state.Catalog.Models;
            Terms = state.Catalog.Terms;
            IdentitySchemes = state.Catalog.IdentitySchemes;
            VocabularyVersion = state.Catalog.VocabularyVersion;
            Vocabularies = state.Catalog.Vocabularies;
            _termRoles = Terms.ToDictionary(item => item.Identifier.AbsoluteUri, item => item.Role,
                StringComparer.Ordinal);
            _identitySchemes = IdentitySchemes.ToDictionary(item => item.Identifier.AbsoluteUri,
                StringComparer.Ordinal);
            return;
        }
        SemanticProvenance[] provenance = state.Fields.Select(item => item.Provenance)
            .Concat(state.Relationships.Select(item => item.Provenance)).ToArray();
        Models = provenance.SelectMany(ReferencedModels)
            .DistinctBy(ModelKey).OrderBy(item => item.Identifier.AbsoluteUri, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal).ToArray();
        Terms = state.Fields.SelectMany(item => new[] { item.Subject.Concept, item.Field })
            .Concat(state.Relationships.SelectMany(item => new[]
                {
                    item.Subject.Concept, item.Relationship, item.Target.Concept
                }))
            .DistinctBy(TermKey).OrderBy(item => item.Identifier.AbsoluteUri, StringComparer.Ordinal).ToArray();
        IdentitySchemes = state.Fields.Select(item => item.Subject.Scheme)
            .Concat(state.Relationships.SelectMany(item => new[] { item.Subject.Scheme, item.Target.Scheme }))
            .DistinctBy(item => item.Identifier.AbsoluteUri, StringComparer.Ordinal)
            .OrderBy(item => item.Identifier.AbsoluteUri, StringComparer.Ordinal).ToArray();
        _termRoles = Terms.ToDictionary(item => item.Identifier.AbsoluteUri, item => item.Role,
            StringComparer.Ordinal);
        _identitySchemes = IdentitySchemes.ToDictionary(item => item.Identifier.AbsoluteUri,
            StringComparer.Ordinal);
    }

    private SemanticCatalogRegistry(IEnumerable<SemanticModelReference> models,
        IEnumerable<SemanticTerm> terms, IEnumerable<SemanticIdentityScheme> identitySchemes,
        SemanticVocabularyCatalogVersion vocabularyVersion,
        IEnumerable<SemanticVocabulary> vocabularies)
    {
        Models = models.DistinctBy(ModelKey).OrderBy(item => item.Identifier.AbsoluteUri, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal).ToArray();
        Terms = terms.DistinctBy(TermKey).OrderBy(item => item.Identifier.AbsoluteUri, StringComparer.Ordinal).ToArray();
        IdentitySchemes = identitySchemes.DistinctBy(item => item.Identifier.AbsoluteUri, StringComparer.Ordinal)
            .OrderBy(item => item.Identifier.AbsoluteUri, StringComparer.Ordinal).ToArray();
        VocabularyVersion = vocabularyVersion;
        Vocabularies = vocabularies.OrderBy(item => item.Identifier.AbsoluteUri, StringComparer.Ordinal)
            .ThenBy(item => item.Version, StringComparer.Ordinal).ToArray();
        _termRoles = Terms.ToDictionary(item => item.Identifier.AbsoluteUri, item => item.Role,
            StringComparer.Ordinal);
        _identitySchemes = IdentitySchemes.ToDictionary(item => item.Identifier.AbsoluteUri,
            StringComparer.Ordinal);
    }

    /// <summary>Returns a registry extended with models, terms and schemes referenced by newly published evidence.</summary>
    public SemanticCatalogRegistry Apply(SemanticDatabaseState state)
    {
        SemanticFieldEvidence[] fields = state.PublicationDelta.Fields
            .Where(item => item.BeginVersion == state.Version).ToArray();
        SemanticRelationshipEvidence[] relationships = state.PublicationDelta.Relationships
            .Where(item => item.BeginVersion == state.Version).ToArray();
        IEnumerable<SemanticProvenance> provenance = fields.Select(item => item.Provenance)
            .Concat(relationships.Select(item => item.Provenance));
        IEnumerable<SemanticModelReference> models = Models.Concat(provenance.SelectMany(ReferencedModels));
        IEnumerable<SemanticTerm> terms = Terms.Concat(fields
            .SelectMany(item => new[] { item.Subject.Concept, item.Field }))
            .Concat(relationships.SelectMany(item => new[]
                {
                    item.Subject.Concept, item.Relationship, item.Target.Concept
                }));
        IEnumerable<SemanticIdentityScheme> schemes = IdentitySchemes
            .Concat(fields.Select(item => item.Subject.Scheme))
            .Concat(relationships.SelectMany(item => new[] { item.Subject.Scheme, item.Target.Scheme }));
        return new(models, terms, schemes, VocabularyVersion, Vocabularies);
    }

    /// <summary>Returns a registry with the supplied vocabulary publication and catalog revision.</summary>
    public SemanticCatalogRegistry Publish(SemanticVocabularyPublicationChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        SemanticVocabulary vocabulary = change.Vocabulary;
        IEnumerable<SemanticVocabulary> vocabularies = Vocabularies
            .Where(item => item.Identifier != vocabulary.Identifier)
            .Append(vocabulary);
        IEnumerable<SemanticModelReference> models = Models.Append(new SemanticModelReference(
            vocabulary.Identifier, vocabulary.Version, SemanticModelKind.Vocabulary));
        return new(models, Terms.Concat(vocabulary.Terms.Select(item => item.Term)),
            IdentitySchemes.Concat(vocabulary.IdentitySchemes), change.CatalogVersion, vocabularies);
    }

    /// <summary>Gets the distinct referenced models ordered by identifier and version.</summary>
    public IReadOnlyList<SemanticModelReference> Models
    {
        get;
    }

    /// <summary>Gets the registered terms ordered by identifier.</summary>
    public IReadOnlyList<SemanticTerm> Terms
    {
        get;
    }

    /// <summary>Gets registered identity schemes ordered by identifier.</summary>
    public IReadOnlyList<SemanticIdentityScheme> IdentitySchemes
    {
        get;
    }

    /// <summary>Gets the published vocabulary catalog version.</summary>
    public SemanticVocabularyCatalogVersion VocabularyVersion
    {
        get;
    }

    /// <summary>Gets the currently published vocabulary definitions.</summary>
    public IReadOnlyList<SemanticVocabulary> Vocabularies { get; } = [];

    private static IEnumerable<SemanticModelReference> ReferencedModels(SemanticProvenance provenance)
    {
        yield return provenance.TargetVocabulary;
        if (provenance.SourceVocabulary is not null)
        {
            yield return provenance.SourceVocabulary;
        }

        foreach (SemanticModelReference mapping in provenance.Mappings)
        {
            yield return mapping;
        }
    }

    /// <summary>Returns a catalog snapshot suitable for inclusion in immutable database state.</summary>
    public SemanticCatalogSnapshot Snapshot() => new(Models, Terms, IdentitySchemes,
        VocabularyVersion, Vocabularies);

    /// <summary>Validates session term roles and identity-scheme definitions against the registered catalog.</summary>
    public void Validate(SemanticSessionEnvelope session)
    {
        ArgumentNullException.ThrowIfNull(session);
        SemanticEntityChange[] changes = session.Pages.SelectMany(page => page.Changes).ToArray();
        SemanticTerm[] terms = changes.SelectMany(change => new[] { change.Subject.Concept }
            .Concat(change.Fields.Select(field => field.Field))
            .Concat(change.Relationships.SelectMany(relationship => relationship.Target is null
                ? new[] { relationship.Relationship }
                : new[] { relationship.Relationship, relationship.Target.Concept }))).ToArray();
        foreach (IGrouping<string, SemanticTerm> group in terms.GroupBy(
                     item => item.Identifier.AbsoluteUri, StringComparer.Ordinal))
        {
            SemanticTermRole[] roles = group.Select(item => item.Role).Distinct().ToArray();
            if (roles.Length != RequiredUniqueDefinitionCount ||
                _termRoles.TryGetValue(group.Key, out SemanticTermRole registered) && registered != roles[0])
            {
                throw new InvalidDataException($"Semantic term '{group.Key}' has conflicting roles.");
            }
        }

        SemanticIdentityScheme[] schemes = changes.Select(change => change.Subject.Scheme)
            .Concat(changes.SelectMany(change => change.Relationships
                .Where(item => item.Target is not null).Select(item => item.Target!.Scheme))).ToArray();
        foreach (IGrouping<string, SemanticIdentityScheme> group in schemes.GroupBy(
                     item => item.Identifier.AbsoluteUri, StringComparer.Ordinal))
        {
            SemanticIdentityScheme[] definitions = group.Distinct().ToArray();
            if (definitions.Length != RequiredUniqueDefinitionCount || _identitySchemes.TryGetValue(group.Key,
                    out SemanticIdentityScheme? registered) && registered != definitions[0])
            {
                throw new InvalidDataException($"Identity scheme '{group.Key}' has conflicting definitions.");
            }
        }
    }

    private static (string Identifier, string Version, SemanticModelKind Kind) ModelKey(
        SemanticModelReference model) => (model.Identifier.AbsoluteUri, model.Version, model.Kind);

    private static (string Identifier, SemanticTermRole Role) TermKey(SemanticTerm term) =>
        (term.Identifier.AbsoluteUri, term.Role);
}
