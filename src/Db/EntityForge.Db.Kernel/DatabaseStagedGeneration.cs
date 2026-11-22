using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace EntityForge.Db.Kernel;

/// <summary>Identifies one profile-defined ownership scope without exposing its data model to the kernel.</summary>
public sealed record DatabaseGenerationScope
{
    /// <summary>Creates a generation ownership scope from nonblank domain and key values.</summary>
    public DatabaseGenerationScope(string domain, string key)
    {
        Domain = Require(domain, nameof(domain));
        Key = Require(key, nameof(key));
    }

    /// <summary>Gets the profile-defined namespace of the ownership scope.</summary>
    public string Domain
    {
        get;
    }

    /// <summary>Gets the profile-defined identity within the scope's domain.</summary>
    public string Key
    {
        get;
    }

    private static string Require(string value, string parameter) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException(
            "A generation scope value is required.", parameter);
}

/// <summary>Identifies one immutable candidate generation within an ownership scope.</summary>
public sealed record DatabaseGenerationIdentity
{
    /// <summary>Creates an immutable generation identity; rejects an empty GUID, revision or definition hash.</summary>
    public DatabaseGenerationIdentity(Guid generationId, string revision, string definitionHash)
    {
        if (generationId == Guid.Empty)
        {
            throw new ArgumentException("A generation identifier is required.", nameof(generationId));
        }

        GenerationId = generationId;
        Revision = !string.IsNullOrWhiteSpace(revision) ? revision : throw new ArgumentException(
            "A generation revision is required.", nameof(revision));
        DefinitionHash = !string.IsNullOrWhiteSpace(definitionHash) ? definitionHash : throw new ArgumentException(
            "A generation definition hash is required.", nameof(definitionHash));
    }

    /// <summary>Gets the unique candidate generation identifier.</summary>
    public Guid GenerationId
    {
        get;
    }

    /// <summary>Gets the revision label bound to this generation.</summary>
    public string Revision
    {
        get;
    }

    /// <summary>Gets the content hash of the definition used to build this generation.</summary>
    public string DefinitionHash
    {
        get;
    }
}

/// <summary>Describes the lifecycle of a privately built generation and its publication.</summary>
public enum DatabaseStagedGenerationStatus
{
    /// <summary>Parts may still be accepted into the hidden generation.</summary>
    Building,
    /// <summary>The exact part manifest has been verified and closed.</summary>
    Sealed,
    /// <summary>This generation is visible as the published generation for its scope.</summary>
    Published,
    /// <summary>A successor has replaced this generation.</summary>
    Retired,
    /// <summary>The unpublished generation has been abandoned.</summary>
    Abandoned
}

/// <summary>Identifies one independently retryable, content-bound part of a staged generation.</summary>
public sealed record DatabaseGenerationPart
{
    /// <summary>Creates a retryable generation part with nonblank identity and content hash.</summary>
    public DatabaseGenerationPart(string partId, string contentHash)
    {
        PartId = !string.IsNullOrWhiteSpace(partId) ? partId : throw new ArgumentException(
            "A generation part identifier is required.", nameof(partId));
        ContentHash = !string.IsNullOrWhiteSpace(contentHash) ? contentHash : throw new ArgumentException(
            "A generation part content hash is required.", nameof(contentHash));
    }

    /// <summary>Gets the identity used to deduplicate part submission.</summary>
    public string PartId
    {
        get;
    }

    /// <summary>Gets the content hash that must match on an exact part retry.</summary>
    public string ContentHash
    {
        get;
    }
}

/// <summary>Declares the exact closed set of parts required before a generation can be published.</summary>
public sealed record DatabaseGenerationManifest
{
    /// <summary>Creates a manifest sorted by ordinal part identity; rejects null parts and duplicate identifiers.</summary>
    public DatabaseGenerationManifest(IReadOnlyList<DatabaseGenerationPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        DatabaseGenerationPart[] ordered = parts
            .Select(part => part ?? throw new ArgumentException("A manifest part cannot be null.", nameof(parts)))
            .OrderBy(part => part.PartId, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Select(part => part.PartId).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new ArgumentException("A generation manifest cannot contain duplicate part identifiers.",
                nameof(parts));
        }

        Parts = ordered;
        Fingerprint = FingerprintParts(ordered);
    }

    /// <summary>Gets the exact required parts in ordinal identifier order.</summary>
    public IReadOnlyList<DatabaseGenerationPart> Parts
    {
        get;
    }

    /// <summary>Gets the SHA-256 fingerprint of the ordered part identities and content hashes.</summary>
    public string Fingerprint
    {
        get;
    }

    private static string FingerprintParts(IReadOnlyList<DatabaseGenerationPart> parts)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write("entityforge.database-generation-manifest.v1");
            writer.Write(parts.Count);
            foreach (DatabaseGenerationPart part in parts)
            {
                writer.Write(part.PartId);
                writer.Write(part.ContentHash);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }
}

/// <summary>Kernel-owned lifecycle metadata; profile payloads remain outside this neutral type.</summary>
public sealed record DatabaseStagedGeneration
{
    internal DatabaseStagedGeneration(DatabaseGenerationScope scope, DatabaseGenerationIdentity identity,
        DatabaseGenerationIdentity? expectedPublishedGeneration, DatabaseStagedGenerationStatus status,
        ImmutableDictionary<string, string> parts, string? manifestFingerprint)
    {
        Scope = scope;
        Identity = identity;
        ExpectedPublishedGeneration = expectedPublishedGeneration;
        Status = status;
        Parts = parts;
        ManifestFingerprint = manifestFingerprint;
    }

    /// <summary>Gets the ownership scope in which this generation is built.</summary>
    public DatabaseGenerationScope Scope
    {
        get; internal init;
    }

    /// <summary>Gets the immutable identity of the candidate generation.</summary>
    public DatabaseGenerationIdentity Identity
    {
        get; internal init;
    }

    /// <summary>Gets the predecessor expected at publication, or null when the scope must have no published generation.</summary>
    public DatabaseGenerationIdentity? ExpectedPublishedGeneration
    {
        get; internal init;
    }

    /// <summary>Gets the generation's current lifecycle state.</summary>
    public DatabaseStagedGenerationStatus Status
    {
        get; internal init;
    }

    /// <summary>Gets accepted part identifiers and their content hashes.</summary>
    public IReadOnlyDictionary<string, string> Parts
    {
        get; internal init;
    }

    /// <summary>Gets the sealed manifest fingerprint, or null before sealing.</summary>
    public string? ManifestFingerprint
    {
        get; internal init;
    }
}

/// <summary>Distinguishes a newly applied lifecycle mutation from an exact idempotent replay.</summary>
public enum DatabaseGenerationMutationOutcome
{
    /// <summary>The mutation changed the catalog.</summary>
    Applied,
    /// <summary>The same identity-bound mutation was already reflected in the catalog.</summary>
    IdempotentReplay
}

/// <summary>Returns an immutable catalog transition together with its affected generation and replay outcome.</summary>
/// <param name="Catalog">The resulting immutable generation catalog.</param>
/// <param name="Generation">The generation affected by the transition.</param>
/// <param name="Outcome">Whether the transition was newly applied or an idempotent replay.</param>
public sealed record DatabaseGenerationMutationResult(
    DatabaseGenerationCatalog Catalog,
    DatabaseStagedGeneration Generation,
    DatabaseGenerationMutationOutcome Outcome);

/// <summary>A persistence-safe snapshot of one neutral generation lifecycle entry.</summary>
/// <param name="Scope">The generation's ownership scope.</param>
/// <param name="Identity">The immutable candidate generation identity.</param>
/// <param name="ExpectedPublishedGeneration">The expected predecessor for compare-and-publish.</param>
/// <param name="Status">The recorded lifecycle state.</param>
/// <param name="Parts">The accepted part identities and content hashes.</param>
/// <param name="ManifestFingerprint">The sealed manifest fingerprint, or null when not yet sealed.</param>
public sealed record DatabaseGenerationSnapshot(
    DatabaseGenerationScope Scope,
    DatabaseGenerationIdentity Identity,
    DatabaseGenerationIdentity? ExpectedPublishedGeneration,
    DatabaseStagedGenerationStatus Status,
    IReadOnlyDictionary<string, string> Parts,
    string? ManifestFingerprint);

/// <summary>
/// Pure neutral state transition model for hidden generations. Persistence and profile payload interpretation are
/// supplied by the owning database runtime.
/// </summary>
public sealed class DatabaseGenerationCatalog
{
    private readonly ImmutableDictionary<Guid, DatabaseStagedGeneration> _generations;
    private readonly ImmutableDictionary<DatabaseGenerationScope, DatabaseGenerationIdentity> _published;

    private DatabaseGenerationCatalog(ImmutableDictionary<Guid, DatabaseStagedGeneration> generations,
        ImmutableDictionary<DatabaseGenerationScope, DatabaseGenerationIdentity> published)
    {
        _generations = generations;
        _published = published;
    }

    /// <summary>Gets an immutable catalog without staged or published generations.</summary>
    public static DatabaseGenerationCatalog Empty
    {
        get;
    }
    = new(
        ImmutableDictionary<Guid, DatabaseStagedGeneration>.Empty,
        ImmutableDictionary<DatabaseGenerationScope, DatabaseGenerationIdentity>.Empty);

    /// <summary>
    /// Restores a previously validated lifecycle snapshot without replaying admission transitions against an empty
    /// publication catalog. The complete graph is validated before any restored state is returned.
    /// </summary>
    public static DatabaseGenerationCatalog Restore(IReadOnlyList<DatabaseGenerationSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var generations = ImmutableDictionary<Guid, DatabaseStagedGeneration>.Empty;
        var identities = new Dictionary<Guid, DatabaseGenerationIdentity>();
        foreach (DatabaseGenerationSnapshot snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(snapshot.Scope);
            ArgumentNullException.ThrowIfNull(snapshot.Identity);
            ArgumentNullException.ThrowIfNull(snapshot.Parts);
            if (!Enum.IsDefined(snapshot.Status))
            {
                throw new InvalidDataException($"Generation '{snapshot.Identity.GenerationId}' has an invalid status.");
            }

            if (!identities.TryAdd(snapshot.Identity.GenerationId, snapshot.Identity))
            {
                throw new InvalidDataException($"Generation '{snapshot.Identity.GenerationId}' is duplicated.");
            }

            if (snapshot.Parts.Any(item => string.IsNullOrWhiteSpace(item.Key) ||
                    string.IsNullOrWhiteSpace(item.Value)))
            {
                throw new InvalidDataException("A restored generation contains an invalid part identity or hash.");
            }

            bool requiresManifest = snapshot.Status is DatabaseStagedGenerationStatus.Sealed or
                DatabaseStagedGenerationStatus.Published or DatabaseStagedGenerationStatus.Retired;
            if (requiresManifest && string.IsNullOrWhiteSpace(snapshot.ManifestFingerprint))
            {
                throw new InvalidDataException("A restored generation has an inconsistent manifest state.");
            }

            if (snapshot.Status is DatabaseStagedGenerationStatus.Building &&
                snapshot.ManifestFingerprint is not null)
            {
                throw new InvalidDataException("A building generation cannot have a completion manifest.");
            }

            if (snapshot.Status is DatabaseStagedGenerationStatus.Abandoned &&
                (snapshot.Parts.Count != 0 || snapshot.ManifestFingerprint is not null))
            {
                throw new InvalidDataException("An abandoned generation must be restored as a compact tombstone.");
            }

            if (requiresManifest && !String.Equals(new DatabaseGenerationManifest(snapshot.Parts
                        .Select(item => new DatabaseGenerationPart(item.Key, item.Value)).ToArray()).Fingerprint,
                    snapshot.ManifestFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A restored generation manifest does not match its parts.");
            }

            generations = generations.Add(snapshot.Identity.GenerationId, new(snapshot.Scope, snapshot.Identity,
                snapshot.ExpectedPublishedGeneration, snapshot.Status,
                snapshot.Parts.ToImmutableDictionary(StringComparer.Ordinal), snapshot.ManifestFingerprint));
        }

        foreach (DatabaseStagedGeneration generation in generations.Values)
        {
            if (generation.ExpectedPublishedGeneration is not { } expected)
            {
                continue;
            }

            if (!identities.TryGetValue(expected.GenerationId, out DatabaseGenerationIdentity? restored) ||
                restored != expected || generations[expected.GenerationId].Scope != generation.Scope)
            {
                throw new InvalidDataException(
                    $"Generation '{generation.Identity.GenerationId}' has an invalid predecessor.");
            }

            if (generations[expected.GenerationId].Status is not (DatabaseStagedGenerationStatus.Published or
                    DatabaseStagedGenerationStatus.Retired))
            {
                throw new InvalidDataException(
                    $"Generation '{generation.Identity.GenerationId}' has a predecessor that was never published.");
            }
        }

        var published = ImmutableDictionary<DatabaseGenerationScope, DatabaseGenerationIdentity>.Empty;
        foreach (DatabaseStagedGeneration generation in generations.Values
                     .Where(item => item.Status is DatabaseStagedGenerationStatus.Published))
        {
            if (published.ContainsKey(generation.Scope))
            {
                throw new InvalidDataException("A generation scope has more than one published generation.");
            }

            published = published.Add(generation.Scope, generation.Identity);
        }
        var reachablePublishedHistory = new HashSet<Guid>();
        foreach (KeyValuePair<DatabaseGenerationScope, DatabaseGenerationIdentity> item in published)
        {
            ValidatePublishedChain(generations, item.Key, item.Value, reachablePublishedHistory);
        }

        if (generations.Values.Any(generation =>
                generation.Status is DatabaseStagedGenerationStatus.Retired &&
                !reachablePublishedHistory.Contains(generation.Identity.GenerationId)))
        {
            throw new InvalidDataException(
                "A retired generation is not part of its scope's current publication chain.");
        }

        return new(generations, published);
    }

    /// <summary>Gets the generations retained by this catalog.</summary>
    public IReadOnlyCollection<DatabaseStagedGeneration> Generations => _generations.Values.ToArray();

    /// <summary>Returns the generation currently published in a scope, or null if none is published.</summary>
    public DatabaseGenerationIdentity? GetPublishedGeneration(DatabaseGenerationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return _published.GetValueOrDefault(scope);
    }

    /// <summary>Returns the generation with the requested identifier; throws if it is absent.</summary>
    public DatabaseStagedGeneration Get(Guid generationId) =>
        _generations.TryGetValue(generationId, out DatabaseStagedGeneration? generation)
            ? generation : throw new KeyNotFoundException($"Generation '{generationId}' does not exist.");

    /// <summary>Attempts to find a generation by its identifier.</summary>
    public bool TryGet(Guid generationId, out DatabaseStagedGeneration? generation) =>
        _generations.TryGetValue(generationId, out generation);

    /// <summary>Begins a hidden generation bound to its scope, immutable definition and expected published predecessor; exact retries are idempotent.</summary>
    public DatabaseGenerationMutationResult Begin(DatabaseGenerationScope scope,
        DatabaseGenerationIdentity identity, DatabaseGenerationIdentity? expectedPublishedGeneration)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(identity);
        if (_generations.TryGetValue(identity.GenerationId, out DatabaseStagedGeneration? existing))
        {
            if (existing.Scope != scope || existing.Identity != identity ||
                existing.ExpectedPublishedGeneration != expectedPublishedGeneration)
            {
                throw new DatabaseGenerationIdentityConflictException(identity.GenerationId);
            }

            return new(this, existing, DatabaseGenerationMutationOutcome.IdempotentReplay);
        }
        RequirePublishedGeneration(scope, expectedPublishedGeneration);
        var created = new DatabaseStagedGeneration(scope, identity, expectedPublishedGeneration,
            DatabaseStagedGenerationStatus.Building,
            ImmutableDictionary.Create<string, string>(StringComparer.Ordinal), null);
        return Applied(created);
    }

    /// <summary>Accepts one content-bound part into a building generation; rejects reuse of a part identity with different content.</summary>
    public DatabaseGenerationMutationResult AcceptPart(Guid generationId, DatabaseGenerationPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        DatabaseStagedGeneration generation = Get(generationId);
        if (generation.Parts.TryGetValue(part.PartId, out string? existingHash))
        {
            if (!String.Equals(existingHash, part.ContentHash, StringComparison.Ordinal))
            {
                throw new DatabaseGenerationPartConflictException(generationId, part.PartId);
            }

            return new(this, generation, DatabaseGenerationMutationOutcome.IdempotentReplay);
        }
        if (generation.Status is not DatabaseStagedGenerationStatus.Building)
        {
            throw new DatabaseGenerationLifecycleException(generationId,
                "Only a building generation can accept a new part.");
        }

        var parts = generation.Parts.ToImmutableDictionary(StringComparer.Ordinal)
            .Add(part.PartId, part.ContentHash);
        return Applied(generation with
        {
            Parts = parts
        });
    }

    /// <summary>Seals a generation only when its accepted parts match the exact supplied manifest.</summary>
    public DatabaseGenerationMutationResult Seal(Guid generationId, DatabaseGenerationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        DatabaseStagedGeneration generation = Get(generationId);
        if (generation.Status is DatabaseStagedGenerationStatus.Sealed or
            DatabaseStagedGenerationStatus.Published or DatabaseStagedGenerationStatus.Retired)
        {
            if (!String.Equals(generation.ManifestFingerprint, manifest.Fingerprint, StringComparison.Ordinal))
            {
                throw new DatabaseGenerationManifestConflictException(generationId);
            }

            return new(this, generation, DatabaseGenerationMutationOutcome.IdempotentReplay);
        }
        if (generation.Status is not DatabaseStagedGenerationStatus.Building)
        {
            throw new DatabaseGenerationLifecycleException(generationId,
                "Only a building generation can be sealed.");
        }

        if (generation.Parts.Count != manifest.Parts.Count || manifest.Parts.Any(part =>
                !generation.Parts.TryGetValue(part.PartId, out string? hash) ||
                !String.Equals(hash, part.ContentHash, StringComparison.Ordinal)))
        {
            throw new DatabaseGenerationManifestConflictException(generationId);
        }

        return Applied(generation with
        {
            Status = DatabaseStagedGenerationStatus.Sealed,
            ManifestFingerprint = manifest.Fingerprint
        });
    }

    /// <summary>Publishes a sealed generation when its expected predecessor still matches, retiring the previous published generation.</summary>
    public DatabaseGenerationMutationResult Publish(Guid generationId)
    {
        DatabaseStagedGeneration generation = Get(generationId);
        if (generation.Status is DatabaseStagedGenerationStatus.Published or
            DatabaseStagedGenerationStatus.Retired)
        {
            return new(this, generation, DatabaseGenerationMutationOutcome.IdempotentReplay);
        }

        if (generation.Status is not DatabaseStagedGenerationStatus.Sealed)
        {
            throw new DatabaseGenerationLifecycleException(generationId,
                "Only a sealed generation can be published.");
        }

        RequirePublishedGeneration(generation.Scope, generation.ExpectedPublishedGeneration);

        ImmutableDictionary<Guid, DatabaseStagedGeneration> generations = _generations;
        foreach (KeyValuePair<Guid, DatabaseStagedGeneration> candidate in generations)
        {
            if (candidate.Value.Scope == generation.Scope &&
                candidate.Value.Status is DatabaseStagedGenerationStatus.Published)
            {
                generations = generations.SetItem(candidate.Key,
                    candidate.Value with
                    {
                        Status = DatabaseStagedGenerationStatus.Retired
                    });
            }
        }

        DatabaseStagedGeneration published = generation with
        {
            Status = DatabaseStagedGenerationStatus.Published
        };
        var catalog = new DatabaseGenerationCatalog(generations.SetItem(generationId, published),
            _published.SetItem(generation.Scope, generation.Identity));
        return new(catalog, published, DatabaseGenerationMutationOutcome.Applied);
    }

    /// <summary>Abandons a generation that has not been published.</summary>
    public DatabaseGenerationMutationResult Abandon(Guid generationId)
    {
        DatabaseStagedGeneration generation = Get(generationId);
        if (generation.Status is DatabaseStagedGenerationStatus.Abandoned)
        {
            return new(this, generation, DatabaseGenerationMutationOutcome.IdempotentReplay);
        }

        if (generation.Status is DatabaseStagedGenerationStatus.Published or
            DatabaseStagedGenerationStatus.Retired)
        {
            throw new DatabaseGenerationLifecycleException(generationId,
                "A generation that has been published cannot be abandoned.");
        }

        return Applied(generation with
        {
            Status = DatabaseStagedGenerationStatus.Abandoned,
            Parts = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal),
            ManifestFingerprint = null
        });
    }

    private DatabaseGenerationMutationResult Applied(DatabaseStagedGeneration generation)
    {
        var catalog = new DatabaseGenerationCatalog(
            _generations.SetItem(generation.Identity.GenerationId, generation), _published);
        return new(catalog, generation, DatabaseGenerationMutationOutcome.Applied);
    }

    private void RequirePublishedGeneration(DatabaseGenerationScope scope, DatabaseGenerationIdentity? expected)
    {
        DatabaseGenerationIdentity? actual = _published.GetValueOrDefault(scope);
        if (actual != expected)
        {
            throw new DatabaseGenerationPublicationConflictException(scope, expected, actual);
        }
    }

    private static void ValidatePublishedChain(
        ImmutableDictionary<Guid, DatabaseStagedGeneration> generations,
        DatabaseGenerationScope scope,
        DatabaseGenerationIdentity published,
        ISet<Guid> reachable)
    {
        var visited = new HashSet<Guid>();
        DatabaseGenerationIdentity? current = published;
        while (current is not null)
        {
            if (!visited.Add(current.GenerationId))
            {
                throw new InvalidDataException("A restored generation publication chain contains a cycle.");
            }

            DatabaseStagedGeneration generation = generations[current.GenerationId];
            if (generation.Scope != scope || generation.Identity != current ||
                generation.Status is not (DatabaseStagedGenerationStatus.Published or
                    DatabaseStagedGenerationStatus.Retired))
            {
                throw new InvalidDataException("A restored generation publication chain is inconsistent.");
            }

            reachable.Add(current.GenerationId);
            current = generation.ExpectedPublishedGeneration;
        }
    }
}

/// <summary>Indicates reuse of a generation identifier with a different scope, definition or predecessor.</summary>
public sealed class DatabaseGenerationIdentityConflictException(Guid generationId) :
    InvalidOperationException($"Generation '{generationId}' was replayed with different identity metadata.");

/// <summary>Indicates that a generation part identity was reused with different content.</summary>
public sealed class DatabaseGenerationPartConflictException(Guid generationId, string partId) :
    InvalidOperationException($"Part '{partId}' of generation '{generationId}' was replayed with different content.");

/// <summary>Indicates that a generation's accepted or sealed parts do not match the required manifest.</summary>
public sealed class DatabaseGenerationManifestConflictException(Guid generationId) :
    InvalidOperationException($"The completion manifest does not match generation '{generationId}'.");

/// <summary>Indicates that the published predecessor changed before the candidate could be published.</summary>
public sealed class DatabaseGenerationPublicationConflictException(
    DatabaseGenerationScope scope, DatabaseGenerationIdentity? expected,
    DatabaseGenerationIdentity? actual) : InvalidOperationException(
        $"Generation scope '{scope.Domain}/{scope.Key}' expected published revision " +
        $"'{expected?.Revision ?? "<none>"}' but found '{actual?.Revision ?? "<none>"}'.")
{
    /// <summary>Gets the ownership scope whose published generation changed.</summary>
    public DatabaseGenerationScope Scope { get; } = scope;

    /// <summary>Gets the predecessor expected by the candidate.</summary>
    public DatabaseGenerationIdentity? Expected { get; } = expected;

    /// <summary>Gets the predecessor observed during publication.</summary>
    public DatabaseGenerationIdentity? Actual { get; } = actual;
}

/// <summary>Indicates a generation transition that is invalid for its current lifecycle state.</summary>
public sealed class DatabaseGenerationLifecycleException(Guid generationId, string message) :
    InvalidOperationException($"Generation '{generationId}': {message}");
