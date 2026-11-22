namespace EntityForge.Semantics;

/// <summary>Evaluates entity changes against the active version of their declared vocabulary.</summary>
public sealed class SemanticVocabularyConformanceValidator
{
    /// <summary>Returns structured findings without changing any database state.</summary>
    public SemanticValidationResult Validate(
        IEnumerable<SemanticEntityChange> changes,
        IEnumerable<SemanticVocabulary> activeVocabularies)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(activeVocabularies);
        IReadOnlyDictionary<(Uri Identifier, string Version), SemanticVocabulary> vocabularies = activeVocabularies
            .ToDictionary(item => (item.Identifier, item.Version));
        var validator = new SemanticChangeValidator();
        var issues = new List<SemanticValidationIssue>();
        foreach (SemanticEntityChange change in changes)
        {
            SemanticModelReference targetVocabulary = change.Provenance.TargetVocabulary;
            if (!vocabularies.TryGetValue((targetVocabulary.Identifier, targetVocabulary.Version),
                    out SemanticVocabulary? vocabulary))
            {
                issues.Add(new SemanticValidationIssue("provenance.targetVocabulary",
                    $"Vocabulary '{targetVocabulary.Identifier}' version '{targetVocabulary.Version}' is not active.",
                    SemanticValidationCodes.UndefinedTerm, subject: change.Subject));
                continue;
            }
            issues.AddRange(validator.Validate(change, vocabulary).Issues);
        }
        return new(issues.AsReadOnly());
    }
}
