namespace EntityForge.Db.Rdf.Contracts;

// An exact subject-producing anchor and complete candidate subjects. Candidates
// include rows rejected by later predicates, preserving absence and phantom reads.
/// <summary>Tracks an exact default-graph predicate/object anchor and the complete state of its candidate subjects.</summary>
/// <remarks>Candidates include subjects rejected by later predicates, so membership changes and previously absent matches remain detectable.</remarks>
public sealed record RdfQueryStateRead : RdfReadPredicate
{
    private const string FingerprintPrefix = "rdf-subject-state:v1:";
    private const int FingerprintCharacters = System.Security.Cryptography.SHA256.HashSizeInBytes * 2;

    /// <summary>Gets the anchor predicate used to discover candidate subjects.</summary>
    public RdfIri Predicate
    {
        get;
    }

    /// <summary>Gets the exact anchor object term.</summary>
    public RdfTerm Object
    {
        get;
    }

    /// <summary>Gets the deduplicated candidate subjects, ordered by canonical resource identity.</summary>
    public IReadOnlyList<RdfTerm> Subjects
    {
        get;
    }

    /// <summary>Gets the version-tagged fingerprint of the captured subject and anchor state.</summary>
    public string ExpectedFingerprint
    {
        get;
    }

    /// <summary>Captures an anchor, candidate subjects and an existing canonical fingerprint.</summary>
    /// <param name="predicate">The default-graph anchor predicate.</param>
    /// <param name="object">The exact object matched by the anchor.</param>
    /// <param name="subjects">All candidate subjects; copied, deduplicated and validated for subject placement.</param>
    /// <param name="expectedFingerprint">The v1 subject-state fingerprint returned by <see cref="Capture"/>.</param>
    /// <exception cref="ArgumentNullException">A required value or a subject entry is null.</exception>
    /// <exception cref="ArgumentException">Term placement or the fingerprint format is invalid.</exception>
    public RdfQueryStateRead(RdfIri predicate, RdfTerm @object, IEnumerable<RdfTerm> subjects, string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        RdfTermValidation.Validate(@object, subject: false);
        Predicate = predicate;
        Object = @object;
        var owned = subjects.Distinct().ToArray();
        foreach (var subject in owned)
        {
            RdfTermValidation.Validate(subject, subject: true);
        }

        Subjects = Array.AsReadOnly(owned.OrderBy(RdfPhysicalCodec.ResourceIdentity, StringComparer.Ordinal).ToArray());
        if (expectedFingerprint.Length != FingerprintPrefix.Length + FingerprintCharacters || !expectedFingerprint.StartsWith(FingerprintPrefix, StringComparison.Ordinal) || !expectedFingerprint[FingerprintPrefix.Length..].All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid query fingerprint.");
        }

        ExpectedFingerprint = expectedFingerprint;
    }

    /// <summary>Captures subjects selected by an exact default-graph predicate/object anchor and their observed state.</summary>
    /// <param name="predicate">The anchor predicate.</param>
    /// <param name="object">The anchor object term.</param>
    /// <param name="relation">The observed RDF relation, including candidate-subject statements.</param>
    /// <returns>A query-state read predicate that can detect membership and candidate-subject changes.</returns>
    public static RdfQueryStateRead Capture(RdfIri predicate, RdfTerm @object, IEnumerable<RdfQuad> relation)
    {
        var quads = relation.ToArray();
        var subjects = quads.Where(q => q.Graph is null && q.Predicate == predicate && q.Object == @object).Select(q => q.Subject).Distinct().ToArray();
        var read = new RdfQueryStateRead(predicate, @object, subjects, FingerprintPrefix + new string('0', FingerprintCharacters));
        return new(predicate, @object, subjects, read.Fingerprint(quads));
    }

    /// <summary>Fingerprints retained candidate subjects and current statements matching the default-graph anchor.</summary>
    /// <param name="relation">The relation to assess against this query dependency.</param>
    /// <returns>A version-tagged subject-state fingerprint for comparison with ExpectedFingerprint.</returns>
    public string Fingerprint(IEnumerable<RdfQuad> relation)
    {
        var subjects = Subjects.ToHashSet();
        return RdfSubjectStateRead.Fingerprint(relation.Where(q => subjects.Contains(q.Subject) ||
            q.Graph is null && q.Predicate == Predicate && q.Object == Object));
    }

    internal static string PredicateResource(RdfIri predicate) => "query:predicate:" + predicate.Value;
}
