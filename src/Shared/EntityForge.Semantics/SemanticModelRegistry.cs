namespace EntityForge.Semantics;

/// <summary>Registers versioned semantic models and checks that mapping and axiom terms are known.</summary>
public sealed class SemanticModelRegistry
{
    private readonly Dictionary<(Uri Identifier, string Version), SemanticVocabulary> _vocabularies = new();
    private readonly Dictionary<(Uri Identifier, string Version), SemanticMappingSet> _mappingSets = new();
    private readonly Dictionary<(Uri Identifier, string Version), SemanticAxiomSet> _axiomSets = new();

    /// <summary>Registers a vocabulary under its exact identifier and version.</summary>
    /// <param name="vocabulary">The immutable model to register.</param>
    /// <exception cref="ArgumentNullException">The model is null.</exception>
    /// <exception cref="InvalidOperationException">The identifier/version is already registered.</exception>
    public void RegisterVocabulary(SemanticVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (!_vocabularies.TryAdd((vocabulary.Identifier, vocabulary.Version), vocabulary))
        {
            throw new InvalidOperationException(
                $"Vocabulary '{vocabulary.Identifier}' version '{vocabulary.Version}' is already registered.");
        }
    }

    /// <summary>Registers a mapping set under its exact identifier and version.</summary>
    /// <param name="mappingSet">The immutable model to register.</param>
    /// <exception cref="ArgumentNullException">The model is null.</exception>
    /// <exception cref="InvalidOperationException">The identifier/version is already registered or a referenced term is absent from the registered vocabularies.</exception>
    public void RegisterMappingSet(SemanticMappingSet mappingSet)
    {
        ArgumentNullException.ThrowIfNull(mappingSet);

        foreach (var mapping in mappingSet.Mappings)
        {
            EnsureKnownTerm(mapping.SourceTerm, "source");
            EnsureKnownTerm(mapping.CanonicalTerm, "canonical");
        }

        if (!_mappingSets.TryAdd((mappingSet.Identifier, mappingSet.Version), mappingSet))
        {
            throw new InvalidOperationException(
                $"Mapping set '{mappingSet.Identifier}' version '{mappingSet.Version}' is already registered.");
        }
    }

    /// <summary>Retrieves a registered vocabulary by exact identifier and version.</summary>
    /// <param name="identifier">Absolute model identifier.</param>
    /// <param name="version">The specific version to retrieve.</param>
    /// <returns>The registered model; no latest-version fallback is performed.</returns>
    /// <exception cref="KeyNotFoundException">No model is registered under that identifier/version.</exception>
    public SemanticVocabulary GetVocabulary(Uri identifier, string version) =>
        _vocabularies.TryGetValue((identifier, version), out var vocabulary)
            ? vocabulary
            : throw new KeyNotFoundException($"Vocabulary '{identifier}' version '{version}' is not registered.");

    /// <summary>Retrieves a registered mapping set by exact identifier and version.</summary>
    /// <param name="identifier">Absolute model identifier.</param>
    /// <param name="version">The specific version to retrieve.</param>
    /// <returns>The registered model; no latest-version fallback is performed.</returns>
    /// <exception cref="KeyNotFoundException">No model is registered under that identifier/version.</exception>
    public SemanticMappingSet GetMappingSet(Uri identifier, string version) =>
        _mappingSets.TryGetValue((identifier, version), out var mappingSet)
            ? mappingSet
            : throw new KeyNotFoundException($"Mapping set '{identifier}' version '{version}' is not registered.");

    /// <summary>Registers a axiom set under its exact identifier and version.</summary>
    /// <param name="axiomSet">The immutable model to register.</param>
    /// <exception cref="ArgumentNullException">The model is null.</exception>
    /// <exception cref="InvalidOperationException">The identifier/version is already registered or a referenced term is absent from the registered vocabularies.</exception>
    public void RegisterAxiomSet(SemanticAxiomSet axiomSet)
    {
        ArgumentNullException.ThrowIfNull(axiomSet);
        foreach (var axiom in axiomSet.Axioms)
        {
            EnsureKnownTerm(axiom.Subject, "axiom subject");
            if (axiom.RelatedTerm is not null)
            {
                EnsureKnownTerm(axiom.RelatedTerm, "axiom object");
            }
        }
        if (!_axiomSets.TryAdd((axiomSet.Identifier, axiomSet.Version), axiomSet))
        {
            throw new InvalidOperationException($"Axiom set '{axiomSet.Identifier}' version '{axiomSet.Version}' is already registered.");
        }
    }

    /// <summary>Retrieves a registered axiom set by exact identifier and version.</summary>
    /// <param name="identifier">Absolute model identifier.</param>
    /// <param name="version">The specific version to retrieve.</param>
    /// <returns>The registered model; no latest-version fallback is performed.</returns>
    /// <exception cref="KeyNotFoundException">No model is registered under that identifier/version.</exception>
    public SemanticAxiomSet GetAxiomSet(Uri identifier, string version) =>
        _axiomSets.TryGetValue((identifier, version), out var set) ? set :
            throw new KeyNotFoundException($"Axiom set '{identifier}' version '{version}' is not registered.");

    private void EnsureKnownTerm(SemanticTerm term, string mappingSide)
    {
        if (!_vocabularies.Values.Any(vocabulary => vocabulary.TryGetTerm(term, out _)))
        {
            throw new InvalidOperationException(
                $"The {mappingSide} term '{term.Identifier}' ({term.Role}) is not registered in a vocabulary.");
        }
    }
}
