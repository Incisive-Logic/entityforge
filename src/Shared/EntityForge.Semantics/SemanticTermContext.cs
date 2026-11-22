namespace EntityForge.Semantics;

/// <summary>Resolves named terms, prefixes and vocabulary-relative names into absolute semantic identifiers.</summary>
public sealed class SemanticTermContext
{
    private readonly IReadOnlyDictionary<string, Uri> prefixes;
    private readonly IReadOnlyDictionary<string, Uri> terms;

    /// <summary>Creates an immutable context from optional default vocabulary, prefixes and exact term aliases.</summary>
    /// <param name="vocabulary">Absolute base identifier to concatenate with otherwise unresolved local names.</param>
    /// <param name="prefixes">Ordinal prefix-name mappings to absolute identifiers.</param>
    /// <param name="terms">Ordinal exact-name mappings to absolute identifiers.</param>
    /// <exception cref="ArgumentException">An identifier is relative, a name is invalid, or a prefix/term name is duplicated.</exception>
    public SemanticTermContext(
        Uri? vocabulary = null,
        IEnumerable<KeyValuePair<string, Uri>>? prefixes = null,
        IEnumerable<KeyValuePair<string, Uri>>? terms = null)
    {
        if (vocabulary is not null && !vocabulary.IsAbsoluteUri)
        {
            throw new ArgumentException("A semantic context vocabulary must be absolute.", nameof(vocabulary));
        }

        Vocabulary = vocabulary;
        this.prefixes = Materialize(prefixes, "prefix");
        this.terms = Materialize(terms, "term");
    }

    /// <summary>The optional absolute default vocabulary used to expand unqualified names.</summary>
    public Uri? Vocabulary
    {
        get;
    }

    /// <summary>Resolves a name using exact aliases, registered prefixes, absolute URI parsing, then the default vocabulary.</summary>
    /// <param name="value">Non-blank alias, prefixed name, absolute identifier or local name.</param>
    /// <param name="role">Semantic role assigned to the resolved identifier.</param>
    /// <returns>The resolved absolute term with the requested role.</returns>
    /// <exception cref="KeyNotFoundException">No alias, prefix, absolute URI or default-vocabulary expansion resolves the name.</exception>
    /// <exception cref="ArgumentException">The name is blank.</exception>
    public SemanticTerm Resolve(string value, SemanticTermRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (terms.TryGetValue(value, out var term))
        {
            return new SemanticTerm(term, role);
        }

        var separator = value.IndexOf(':');
        if (separator > 0 && prefixes.TryGetValue(value[..separator], out var prefix))
        {
            return new SemanticTerm(Expand(prefix, value[(separator + 1)..]), role);
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return new SemanticTerm(absolute, role);
        }

        if (Vocabulary is not null)
        {
            return new SemanticTerm(Expand(Vocabulary, value), role);
        }

        throw new KeyNotFoundException($"Semantic term '{value}' cannot be resolved by this context.");
    }

    private static IReadOnlyDictionary<string, Uri> Materialize(
        IEnumerable<KeyValuePair<string, Uri>>? values,
        string kind)
    {
        var result = new Dictionary<string, Uri>(StringComparer.Ordinal);
        foreach (var (name, identifier) in values ?? Array.Empty<KeyValuePair<string, Uri>>())
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(identifier);
            if (name.StartsWith('@') || name.Contains(':'))
            {
                throw new ArgumentException($"A semantic context {kind} name cannot be a keyword or contain ':'.", nameof(values));
            }

            if (!identifier.IsAbsoluteUri)
            {
                throw new ArgumentException($"A semantic context {kind} identifier must be absolute.", nameof(values));
            }

            if (!result.TryAdd(name, identifier))
            {
                throw new ArgumentException($"Semantic context {kind} '{name}' is duplicated.", nameof(values));
            }
        }

        return result;
    }

    private static Uri Expand(Uri prefix, string suffix) =>
        new(prefix.AbsoluteUri + suffix, UriKind.Absolute);
}
