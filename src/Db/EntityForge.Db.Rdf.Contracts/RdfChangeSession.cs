using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>A complete replacement of one subject in one graph. A null graph means only the default graph.</summary>
/// <param name="Subject">Subject whose selected graph contribution is replaced.</param>
/// <param name="Graph">Named graph, or null for the default graph.</param>
/// <param name="Quads">Complete replacement quad set for the selected subject and graph.</param>
public sealed record RdfSubjectReplacement(RdfTerm Subject, RdfTerm? Graph, IReadOnlyList<RdfQuad> Quads);

/// <summary>Detached native RDF changes, supplied without a POCO or entity mapping.</summary>
/// <remarks>
/// Factory methods serialize the supplied request without accessing a database. Submit the session through
/// the RDF connection's change API. Reuse its operation identity only for retries of the same logical request.
/// Replacement scopes are resolved against a snapshot during application, with captured subject-state reads
/// used for concurrency validation. Creating a session is not a commit or a guarantee that later validation succeeds.
/// </remarks>
public sealed class RdfChangeSession
{
    /// <summary>Maximum supplied or expanded mutations in one bounded change session.</summary>
    public const int MaximumChanges = 1_000;
    private const int FormatVersion = 1;
    private readonly byte[] _request;

    internal byte[] RequestBytes() => _request.ToArray();

    internal static RdfChangeSession RestoreRequest(byte[] bytes)
    {
        if (bytes.Length > EntityRecordDeliveryCodec.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Oversized native request.");
        }

        var definition = JsonSerializer.Deserialize<Definition>(bytes) ?? throw new InvalidDataException("Missing native request.");
        if (definition.Version != FormatVersion || definition.OperationId == Guid.Empty)
        {
            throw new InvalidDataException("Invalid native request version or identity.");
        }

        if ((definition.Incremental is null) == (definition.Replacements is null))
        {
            throw new InvalidDataException("Expected exactly one native change form.");
        }

        var result = definition.Incremental is { } payload
            ? Incremental(RdfTransactionCodec.DecodeTransaction(definition.OperationId, payload))
            : Replace(definition.OperationId, definition.Replacements!);
        if (!result._request.AsSpan().SequenceEqual(bytes))
        {
            throw new InvalidDataException("Noncanonical native request.");
        }

        return result;
    }

    internal Definition ReadDefinition() => JsonSerializer.Deserialize<Definition>(_request)!;

    internal sealed record Definition(int Version, Guid OperationId, byte[]? Incremental, RdfSubjectReplacement[]? Replacements);

    private RdfChangeSession(Definition definition)
    {
        if (definition.OperationId == Guid.Empty)
        {
            throw new ArgumentException("An operation identity is required.");
        }

        _request = JsonSerializer.SerializeToUtf8Bytes(definition);
        if (_request.Length > EntityRecordDeliveryCodec.MaximumPayloadBytes)
        {
            throw new InvalidOperationException("The RDF change session exceeds its byte limit.");
        }

        OperationId = definition.OperationId;
    }

    /// <summary>Gets the stable logical session identity used for retry and resolution.</summary>
    public Guid OperationId
    {
        get;
    }

    /// <summary>Captures explicit assertions/retractions and their native concurrency predicates.</summary>
    /// <param name="transaction">The native transaction, including its stable identity, mutations and read predicates.</param>
    /// <returns>A detached session containing a serialized copy of the transaction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is null.</exception>
    /// <exception cref="ArgumentException">The transaction requests initial bulk loading or carries mapping or source-command metadata.</exception>
    /// <exception cref="InvalidOperationException">The mutation count or serialized request exceeds its limit.</exception>
    /// <remarks>The captured bytes are independent of subsequent changes to caller-owned collections. Current database state is not consulted by this factory.</remarks>
    public static RdfChangeSession Incremental(RdfTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.IsInitialBulkLoad || transaction.InterpretationFingerprint is not null || transaction.SourceCommandHash is not null)
        {
            throw new ArgumentException("A change session cannot impersonate a mapped, bulk-load or source-command operation.", nameof(transaction));
        }

        if (transaction.Mutations.Count > MaximumChanges)
        {
            throw new InvalidOperationException("The RDF change session exceeds its mutation limit.");
        }

        return new(new(FormatVersion, transaction.TransactionId, RdfTransactionCodec.Encode(transaction), null));
    }

    /// <summary>Captures complete subject states. Empty quads clear the declared subject/graph; undeclared scopes remain unchanged.</summary>
    /// <param name="operationId">Nonempty identity retained for retries of this logical replacement operation.</param>
    /// <param name="subjects">Unique subject/graph scopes with their complete desired quad sets. A null graph selects only the default graph.</param>
    /// <returns>A detached session with deduplicated, canonically ordered replacement quads and scopes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="subjects"/> is null.</exception>
    /// <exception cref="ArgumentException">The operation identity or scopes are invalid, scopes repeat, a quad lies outside its declared scope, or supplied counts exceed <see cref="MaximumChanges"/>.</exception>
    /// <exception cref="InvalidOperationException">The serialized request exceeds its byte limit.</exception>
    /// <remarks>
    /// Counts are checked before duplicate quads are removed. Applying the session computes assertions and
    /// retractions from the selected snapshot; that expanded delta must also fit <see cref="MaximumChanges"/>.
    /// Clearing a large existing subject can therefore fail during preparation even when its supplied replacement
    /// is empty. No other subject or graph is implicitly cleared.
    /// </remarks>
    public static RdfChangeSession Replace(Guid operationId, IReadOnlyList<RdfSubjectReplacement> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        if (subjects.Count is < 1 or > MaximumChanges || subjects.Any(s => s is null || s.Quads is null) ||
            subjects.Sum(s => (long)s.Quads.Count) > MaximumChanges)
        {
            throw new ArgumentException("Invalid or oversized RDF replacement scopes.", nameof(subjects));
        }

        if (subjects.Select(s => (s.Subject, s.Graph)).Distinct().Count() != subjects.Count)
        {
            throw new ArgumentException("An RDF replacement scope cannot be repeated.", nameof(subjects));
        }

        foreach (var subject in subjects)
        {
            _ = new RdfQuad(subject.Subject, new("urn:entityforge:scope-validation"), subject.Subject, subject.Graph);
            if (subject.Quads.Any(q => q is null || q.Subject != subject.Subject || q.Graph != subject.Graph))
            {
                throw new ArgumentException("A replacement contains a quad outside its declared subject and graph.", nameof(subjects));
            }
        }
        var canonical = subjects.Select(subject => subject with
        {
            Quads = subject.Quads.Distinct().OrderBy(quad => JsonSerializer.Serialize(quad), StringComparer.Ordinal).ToArray()
        }).OrderBy(subject => JsonSerializer.Serialize(new { subject.Subject, subject.Graph }), StringComparer.Ordinal).ToArray();
        return new(new(FormatVersion, operationId, null, canonical));
    }
}
