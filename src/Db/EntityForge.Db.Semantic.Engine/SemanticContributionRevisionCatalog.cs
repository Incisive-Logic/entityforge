using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed record SemanticContributionRevisionMutation(
    DatabaseGenerationMutationOutcome Outcome,
    DatabaseStagedGeneration Generation);

internal sealed record SemanticContributionRevisionPublication(
    DatabaseGenerationMutationOutcome Outcome,
    DatabaseStagedGeneration Generation,
    SemanticSessionEnvelope? Session);

internal sealed record SemanticContributionRevisionSnapshot(
    SemanticContributionScope Scope,
    DatabaseGenerationIdentity Identity,
    SemanticChangeSessionStart? Start,
    IReadOnlyList<SemanticChangeSessionPage> Pages,
    SemanticChangeSessionClose? Close,
    DatabaseGenerationIdentity? ExpectedPublishedGeneration,
    DatabaseStagedGenerationStatus Status,
    IReadOnlyDictionary<string, string> PartHashes,
    string? ManifestFingerprint);

/// <summary>
/// Binds Semantic source-snapshot ownership to the neutral generation lifecycle. This catalog currently models the
/// exact transitions and retained payload; its transitions become durable only when driven through the profile WAL.
/// </summary>
internal sealed class SemanticContributionRevisionCatalog
{
    private sealed record Assembly(SemanticContributionScope Scope,
        DatabaseGenerationIdentity Identity, SemanticChangeSessionStart? Start,
        Dictionary<int, SemanticChangeSessionPage> Pages, SemanticChangeSessionClose? Close,
        long PayloadBytes);

    private readonly SemanticChangeSessionTransportCodec _codec = new();
    private readonly SemanticContributionRevisionLimits _limits;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Assembly> _assemblies = [];
    private DatabaseGenerationCatalog _catalog = DatabaseGenerationCatalog.Empty;

    public SemanticContributionRevisionCatalog(SemanticContributionRevisionLimits? limits = null)
    {
        _limits = limits ?? SemanticContributionRevisionLimits.Default;
        if (_limits.MaximumActiveRevisions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "The active revision limit must be positive.");
        }

        if (_limits.MaximumRetainedRevisions < _limits.MaximumActiveRevisions)
        {
            throw new ArgumentOutOfRangeException(nameof(limits),
                "The retained revision limit cannot be smaller than the active revision limit.");
        }

        if (_limits.MaximumRetainedRevisions > SemanticContributionRevisionLimits.FormatMaximumCollectionEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(limits),
                "The retained revision limit exceeds the durable format bound.");
        }

        if (_limits.MaximumPartsPerRevision <= 0 ||
            _limits.MaximumPartsPerRevision > SemanticContributionRevisionLimits.FormatMaximumCollectionEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(limits),
                "The part limit must be positive and fit the durable format bound.");
        }

        if (_limits.MaximumBytesPerRevision <= 0 ||
            _limits.MaximumTotalRetainedBytes < _limits.MaximumBytesPerRevision)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "The staged byte limits are invalid.");
        }
    }

    public DatabaseGenerationIdentity? GetPublished(SemanticChangeSessionSource source)
    {
        lock (_gate)
        {
            return _catalog.GetPublishedGeneration(Scope(source));
        }
    }

    public DatabaseGenerationIdentity? GetPublished(SemanticContributionScope scope)
    {
        lock (_gate)
        {
            return _catalog.GetPublishedGeneration(Scope(scope.SourceSystem, scope.SourcePartition));
        }
    }

    public SemanticContributionRevisionInfo? Get(Guid generationId)
    {
        lock (_gate)
        {
            if (!_catalog.TryGet(generationId, out DatabaseStagedGeneration? generation))
            {
                return null;
            }

            Assembly assembly = AssemblyFor(generationId);
            return new(assembly.Scope,
                FromKernel(generation!.Identity)!, FromKernel(generation.ExpectedPublishedGeneration),
                (SemanticContributionRevisionState)generation.Status);
        }
    }

    public bool IsManaged(SemanticChangeSessionSource source)
    {
        lock (_gate)
        {
            return _catalog.Generations.Any(item => item.Scope == Scope(source));
        }
    }

    public SemanticSessionEnvelope GetSealedSession(Guid generationId)
    {
        lock (_gate)
        {
            Assembly assembly = AssemblyFor(generationId);
            if (assembly.Start is null || assembly.Close is null)
            {
                throw new DatabaseGenerationLifecycleException(generationId,
                    "A Semantic contribution revision must retain a sealed assembly before publication.");
            }

            return new(assembly.Start, OrderedPages(assembly, assembly.Close), assembly.Close);
        }
    }

    public SemanticSessionEnvelope? Validate(SemanticContributionRevisionChange change)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(change);
            switch (change.Kind)
            {
                case SemanticContributionRevisionChangeKind.Begin:
                    if (change.Start is null || change.Start.SessionId != change.GenerationId)
                    {
                        throw new InvalidDataException("A contribution-revision begin has inconsistent identity.");
                    }

                    ValidateStart(change.Start);
                    if (!_catalog.TryGet(change.GenerationId, out _))
                    {
                        EnsureBeginCapacity(change.Start, change.ExpectedPublishedRevision);
                    }

                    _catalog.Begin(Scope(change.Start.Source), Identity(change.Start),
                        ToKernel(change.ExpectedPublishedRevision));
                    return null;
                case SemanticContributionRevisionChangeKind.AcceptPart:
                    if (change.Page is null || change.Page.SessionId != change.GenerationId)
                    {
                        throw new InvalidDataException("A contribution-revision part has inconsistent identity.");
                    }

                    ValidatePartAdmission(change.Page);
                    return null;
                case SemanticContributionRevisionChangeKind.Seal:
                    if (change.Close is null || change.Close.SessionId != change.GenerationId)
                    {
                        throw new InvalidDataException("A contribution-revision seal has inconsistent identity.");
                    }

                    Assembly assembly = AssemblyFor(change.GenerationId);
                    if (assembly.Close is not null)
                    {
                        if (assembly.Close != change.Close)
                        {
                            throw new InvalidDataException(
                                "A sealed Semantic contribution revision was replayed with a different close.");
                        }

                        _catalog.Seal(change.GenerationId, ManifestFromCatalog(change.GenerationId));
                        return null;
                    }
                    SemanticChangeSessionStart start = assembly.Start ?? throw new InvalidDataException(
                        "An unsealed contribution revision is missing its start message.");
                    SemanticChangeSessionPage[] pages = OrderedPages(assembly, change.Close);
                    ValidateClose(start, pages, change.Close);
                    ValidateSealAdmission(change.GenerationId, change.Close, Manifest(pages));
                    _catalog.Seal(change.GenerationId, Manifest(pages));
                    return null;
                case SemanticContributionRevisionChangeKind.Publish:
                    DatabaseGenerationMutationResult publication = _catalog.Publish(change.GenerationId);
                    return publication.Outcome is DatabaseGenerationMutationOutcome.IdempotentReplay
                        ? null : GetSealedSession(change.GenerationId);
                case SemanticContributionRevisionChangeKind.Abandon:
                    _catalog.Abandon(change.GenerationId);
                    return null;
                default:
                    throw new InvalidDataException($"Unsupported contribution-revision change kind '{change.Kind}'.");
            }
        }
    }

    public IReadOnlyList<SemanticContributionRevisionSnapshot> Capture()
    {
        lock (_gate)
        {
            return _catalog.Generations.Select(generation =>
        {
            Assembly assembly = AssemblyFor(generation.Identity.GenerationId);
            return new SemanticContributionRevisionSnapshot(assembly.Scope, generation.Identity, assembly.Start,
                assembly.Pages.OrderBy(item => item.Key).Select(item => item.Value).ToArray(),
                assembly.Close, generation.ExpectedPublishedGeneration, generation.Status,
                generation.Parts, generation.ManifestFingerprint);
        }).ToArray();
        }
    }

    public void Restore(IReadOnlyList<SemanticContributionRevisionSnapshot> snapshots)
    {
        lock (_gate)
        {
            _assemblies.Clear();
            var restored = new List<DatabaseGenerationSnapshot>(snapshots.Count);
            foreach (SemanticContributionRevisionSnapshot snapshot in snapshots)
            {
                ValidateSnapshotIdentity(snapshot);
                if (snapshot.Pages.Zip(snapshot.Pages.Skip(1),
                        (left, right) => left.PageNumber >= right.PageNumber).Any(invalid => invalid))
                {
                    throw new InvalidDataException("Restored contribution pages must be uniquely ordered.");
                }

                Dictionary<int, SemanticChangeSessionPage> pages = snapshot.Pages.ToDictionary(
                    page => page.PageNumber);
                long payloadBytes = snapshot.Pages.Sum(page => (long)_codec.Serialize(page).Length);
                ValidateRestoredPayload(snapshot, pages, payloadBytes);
                _assemblies.Add(snapshot.Identity.GenerationId, new(snapshot.Scope, snapshot.Identity,
                    snapshot.Start, pages, snapshot.Close, payloadBytes));
                restored.Add(new(Scope(snapshot.Scope.SourceSystem, snapshot.Scope.SourcePartition), snapshot.Identity,
                    snapshot.ExpectedPublishedGeneration, snapshot.Status, snapshot.PartHashes,
                    snapshot.ManifestFingerprint));
            }
            _catalog = DatabaseGenerationCatalog.Restore(restored);
            EnsureAggregateLimits();
        }
    }

    public SemanticContributionRevisionMutation Begin(SemanticChangeSessionStart start,
        DatabaseGenerationIdentity? expectedPublishedGeneration)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(start);
            ValidateStart(start);
            DatabaseGenerationIdentity identity = Identity(start);
            if (!_catalog.TryGet(identity.GenerationId, out _))
            {
                EnsureBeginCapacity(start, FromKernel(expectedPublishedGeneration));
            }

            DatabaseGenerationMutationResult result = _catalog.Begin(Scope(start.Source), identity,
                expectedPublishedGeneration);
            if (result.Outcome is DatabaseGenerationMutationOutcome.Applied)
            {
                _assemblies.Add(start.SessionId, new(
                    new(start.Source.SourceSystem, start.Source.SourcePartition), identity, start, [], null, 0));
            }
            else if (!_assemblies.ContainsKey(start.SessionId))
            {
                throw new InvalidDataException("A replayed Semantic contribution revision has no retained assembly.");
            }

            _catalog = result.Catalog;
            return new(result.Outcome, result.Generation);
        }
    }

    public SemanticContributionRevisionMutation AcceptPart(SemanticChangeSessionPage page)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(page);
            Assembly assembly = AssemblyFor(page.SessionId);
            byte[] encoded = _codec.Serialize(page);
            if (encoded.Length > SemanticContributionRevisionLimits.FormatMaximumEmbeddedPartBytes)
            {
                throw new InvalidDataException("A contribution revision page exceeds the durable format bound.");
            }

            var part = new DatabaseGenerationPart(PartId(page.PageNumber), Hash(encoded));
            ValidatePartAdmission(page);
            DatabaseGenerationMutationResult result = _catalog.AcceptPart(page.SessionId, part);
            if (result.Outcome is DatabaseGenerationMutationOutcome.Applied)
            {
                long revisionBytes = checked(assembly.PayloadBytes + encoded.Length);
                assembly.Pages.Add(page.PageNumber, page);
                _assemblies[page.SessionId] = assembly with
                {
                    PayloadBytes = revisionBytes
                };
            }
            _catalog = result.Catalog;
            return new(result.Outcome, result.Generation);
        }
    }

    public SemanticContributionRevisionMutation Seal(SemanticChangeSessionClose close)
    {
        lock (_gate)
        {
            ArgumentNullException.ThrowIfNull(close);
            Assembly assembly = AssemblyFor(close.SessionId);
            if (assembly.Close is not null)
            {
                if (assembly.Close != close)
                {
                    throw new InvalidDataException("A sealed Semantic contribution revision was replayed with a different close.");
                }

                DatabaseGenerationMutationResult replay = _catalog.Seal(close.SessionId,
                    ManifestFromCatalog(close.SessionId));
                return new(replay.Outcome, replay.Generation);
            }
            SemanticChangeSessionStart start = assembly.Start ?? throw new InvalidDataException(
                "An unsealed contribution revision is missing its start message.");
            SemanticChangeSessionPage[] pages = OrderedPages(assembly, close);
            ValidateClose(start, pages, close);
            DatabaseGenerationManifest manifest = Manifest(pages);
            ValidateSealAdmission(close.SessionId, close, manifest);
            DatabaseGenerationMutationResult result = _catalog.Seal(close.SessionId, manifest);
            if (result.Outcome is DatabaseGenerationMutationOutcome.Applied)
            {
                _assemblies[close.SessionId] = assembly with
                {
                    Close = close
                };
            }
            else if (assembly.Close != close)
            {
                throw new InvalidDataException("A sealed Semantic contribution revision was replayed with a different close.");
            }

            _catalog = result.Catalog;
            return new(result.Outcome, result.Generation);
        }
    }

    public SemanticContributionRevisionPublication Publish(Guid generationId)
    {
        lock (_gate)
        {
            DatabaseGenerationMutationResult result = _catalog.Publish(generationId);
            SemanticSessionEnvelope? session = result.Outcome is DatabaseGenerationMutationOutcome.Applied
                ? GetSealedSession(generationId) : null;
            _catalog = result.Catalog;
            if (result.Outcome is DatabaseGenerationMutationOutcome.Applied)
            {
                ReleaseTerminalPayloads(result.Generation.Scope);
            }

            return new(result.Outcome, result.Generation, session);
        }
    }

    public SemanticContributionRevisionMutation Abandon(Guid generationId)
    {
        lock (_gate)
        {
            DatabaseGenerationMutationResult result = _catalog.Abandon(generationId);
            _catalog = result.Catalog;
            if (result.Outcome is DatabaseGenerationMutationOutcome.Applied)
            {
                Assembly assembly = AssemblyFor(generationId);
                _assemblies[generationId] = assembly with
                {
                    Start = null,
                    Pages = [],
                    Close = null,
                    PayloadBytes = 0
                };
            }
            return new(result.Outcome, result.Generation);
        }
    }

    private Assembly AssemblyFor(Guid generationId) =>
        _assemblies.TryGetValue(generationId, out Assembly? assembly) ? assembly :
            throw new KeyNotFoundException($"Semantic contribution revision '{generationId}' does not exist.");

    private static SemanticChangeSessionPage[] OrderedPages(Assembly assembly,
        SemanticChangeSessionClose close)
    {
        if (close.PageCount < 0)
        {
            throw new InvalidDataException("A contribution revision has a negative page count.");
        }

        if (close.PageCount != assembly.Pages.Count)
        {
            throw new InvalidDataException("Semantic contribution revision page count does not match accepted pages.");
        }

        var pages = new SemanticChangeSessionPage[close.PageCount];
        for (int pageNumber = 0; pageNumber < pages.Length; pageNumber++)
        {
            if (!assembly.Pages.TryGetValue(pageNumber, out pages[pageNumber]!))
            {
                throw new InvalidDataException($"Semantic contribution revision is missing page {pageNumber}.");
            }
        }

        if (assembly.Pages.Count != pages.Length)
        {
            throw new InvalidDataException("Semantic contribution revision contains a page outside its manifest.");
        }

        return pages;
    }

    private static void ValidateClose(SemanticChangeSessionStart start,
        IReadOnlyList<SemanticChangeSessionPage> pages, SemanticChangeSessionClose close)
    {
        if (pages.Sum(page => page.Changes.Count) != close.ChangeCount)
        {
            throw new InvalidDataException("Semantic contribution revision change count does not match its pages.");
        }

        if (!String.Equals(SemanticChangeSessionContentHasher.Compute(pages), close.ContentHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Semantic contribution revision content hash does not match its pages.");
        }

        string operationHash = SemanticChangeSessionContentHasher.ComputeOperation(start, pages);
        if (close.OperationHash is null || !String.Equals(operationHash, close.OperationHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Semantic contribution revision requires an exact operation hash.");
        }
    }

    private static void ValidateStart(SemanticChangeSessionStart start)
    {
        if (start.Kind is not SemanticChangeSessionKind.SourceSnapshot)
        {
            throw new ArgumentException("Only a complete Semantic source snapshot can build a contribution revision.",
                nameof(start));
        }

        if (start.ProgressPrecondition is not null)
        {
            throw new ArgumentException(
                "A contribution revision uses conditional publication, not an ordered source-progress guard.",
                nameof(start));
        }
    }

    private DatabaseGenerationIdentity Identity(SemanticChangeSessionStart start) =>
        new(start.SessionId, start.Source.SourceVersion, Hash(_codec.Serialize(start)));

    private DatabaseGenerationManifest Manifest(IReadOnlyList<SemanticChangeSessionPage> pages) => new(
        pages.Select(page => new DatabaseGenerationPart(PartId(page.PageNumber),
            Hash(_codec.Serialize(page)))).ToArray());

    private DatabaseGenerationManifest ManifestFromCatalog(Guid generationId) => new(_catalog.Get(generationId)
        .Parts.OrderBy(item => item.Key, StringComparer.Ordinal)
        .Select(item => new DatabaseGenerationPart(item.Key, item.Value)).ToArray());

    private void EnsureBeginCapacity(SemanticChangeSessionStart start,
        SemanticContributionRevision? expectedPublishedRevision)
    {
        if (_catalog.Generations.Count >= _limits.MaximumRetainedRevisions)
        {
            throw new InvalidDataException("The retained contribution revision limit has been reached.");
        }

        int active = _catalog.Generations.Count(item => item.Status is DatabaseStagedGenerationStatus.Building or
            DatabaseStagedGenerationStatus.Sealed);
        if (active >= _limits.MaximumActiveRevisions)
        {
            throw new InvalidDataException("The active contribution revision limit has been reached.");
        }

        DatabaseGenerationIdentity identity = Identity(start);
        if (_codec.Serialize(start).Length > SemanticContributionRevisionLimits.FormatMaximumEmbeddedPartBytes)
        {
            throw new InvalidDataException("A contribution revision start exceeds the durable format bound.");
        }

        long metadataBytes = MetadataBytes(new(start.Source.SourceSystem, start.Source.SourcePartition),
            identity, start, null, ToKernel(expectedPublishedRevision),
            new Dictionary<string, string>(), null);
        ValidateResourceEnvelope(metadataBytes, checked(TotalRevisionBytes() + metadataBytes));
    }

    private void ValidatePartAdmission(SemanticChangeSessionPage page)
    {
        if (page.PageNumber < 0 || page.PageNumber >= _limits.MaximumPartsPerRevision)
        {
            throw new InvalidDataException("A contribution revision page number exceeds its configured bound.");
        }

        Assembly assembly = AssemblyFor(page.SessionId);
        byte[] encoded = _codec.Serialize(page);
        DatabaseGenerationMutationResult preview = _catalog.AcceptPart(page.SessionId,
            new(PartId(page.PageNumber), Hash(encoded)));
        if (preview.Outcome is DatabaseGenerationMutationOutcome.IdempotentReplay)
        {
            return;
        }

        long addedBytes = checked(encoded.Length + PartMetadataBytes(
            new DatabaseGenerationPart(PartId(page.PageNumber), Hash(encoded))));
        long revisionBytes = checked(RevisionBytes(_catalog.Get(page.SessionId), assembly) + addedBytes);
        long totalBytes = checked(TotalRevisionBytes() + addedBytes);
        if (assembly.Pages.Count >= _limits.MaximumPartsPerRevision ||
            revisionBytes > _limits.MaximumBytesPerRevision ||
            totalBytes > _limits.MaximumTotalRetainedBytes)
        {
            throw new InvalidDataException("Contribution revision staging exceeds its configured resource limits.");
        }
    }

    private long TotalRevisionBytes() => _catalog.Generations.Sum(generation =>
        RevisionBytes(generation, AssemblyFor(generation.Identity.GenerationId)));

    private void EnsureAggregateLimits()
    {
        if (_catalog.Generations.Count(item => item.Status is DatabaseStagedGenerationStatus.Building or
                DatabaseStagedGenerationStatus.Sealed) > _limits.MaximumActiveRevisions ||
            _catalog.Generations.Count > _limits.MaximumRetainedRevisions ||
            _catalog.Generations.Any(generation => RevisionBytes(generation,
                AssemblyFor(generation.Identity.GenerationId)) > _limits.MaximumBytesPerRevision) ||
            TotalRevisionBytes() > _limits.MaximumTotalRetainedBytes)
        {
            throw new InvalidDataException("The restored contribution revision catalog exceeds configured limits.");
        }
    }

    private void ValidateRestoredPayload(SemanticContributionRevisionSnapshot snapshot,
        IReadOnlyDictionary<int, SemanticChangeSessionPage> pages, long payloadBytes)
    {
        if (pages.Count > _limits.MaximumPartsPerRevision ||
            snapshot.PartHashes.Count > _limits.MaximumPartsPerRevision ||
            pages.Keys.Any(number => number < 0 || number >= _limits.MaximumPartsPerRevision) ||
            checked(payloadBytes + MetadataBytes(snapshot.Scope, snapshot.Identity, snapshot.Start,
                snapshot.Close, snapshot.ExpectedPublishedGeneration, snapshot.PartHashes,
                snapshot.ManifestFingerprint)) > _limits.MaximumBytesPerRevision ||
            pages.Values.Any(page =>
                _codec.Serialize(page).Length > SemanticContributionRevisionLimits.FormatMaximumEmbeddedPartBytes))
        {
            throw new InvalidDataException("A restored contribution revision exceeds configured limits.");
        }

        bool active = snapshot.Status is DatabaseStagedGenerationStatus.Building or
            DatabaseStagedGenerationStatus.Sealed;
        if (active && snapshot.Start is null)
        {
            throw new InvalidDataException("An active contribution revision is missing its start message.");
        }

        if (!active && snapshot.Start is not null)
        {
            throw new InvalidDataException("A terminal contribution revision retained unnecessary start payload.");
        }

        if (!active && pages.Count != 0)
        {
            throw new InvalidDataException("A terminal contribution revision retained page payloads.");
        }

        if (pages.Values.Any(page => page.SessionId != snapshot.Identity.GenerationId) ||
            snapshot.Close is not null && snapshot.Close.SessionId != snapshot.Identity.GenerationId)
        {
            throw new InvalidDataException("Restored contribution payload identity does not match its revision.");
        }

        foreach (KeyValuePair<int, SemanticChangeSessionPage> page in pages)
        {
            if (!snapshot.PartHashes.TryGetValue(PartId(page.Key), out string? hash) ||
                !String.Equals(hash, Hash(_codec.Serialize(page.Value)), StringComparison.Ordinal))
            {
                throw new InvalidDataException("A restored contribution revision page does not match its part hash.");
            }
        }

        if (snapshot.Status is DatabaseStagedGenerationStatus.Building or DatabaseStagedGenerationStatus.Sealed &&
            pages.Count != snapshot.PartHashes.Count)
        {
            throw new InvalidDataException("A restorable active contribution revision is missing page payloads.");
        }

        switch (snapshot.Status)
        {
            case DatabaseStagedGenerationStatus.Building:
            case DatabaseStagedGenerationStatus.Abandoned:
                if (snapshot.Close is not null || snapshot.ManifestFingerprint is not null ||
                    snapshot.Status is DatabaseStagedGenerationStatus.Abandoned && snapshot.PartHashes.Count != 0)
                {
                    throw new InvalidDataException("An unsealed revision retained sealed lifecycle metadata.");
                }

                break;
            case DatabaseStagedGenerationStatus.Sealed:
            case DatabaseStagedGenerationStatus.Published:
            case DatabaseStagedGenerationStatus.Retired:
                SemanticChangeSessionClose close = snapshot.Close ?? throw new InvalidDataException(
                    "A sealed or published revision is missing its close message.");
                if (close.PageCount != snapshot.PartHashes.Count || close.ChangeCount < 0 ||
                    String.IsNullOrEmpty(close.OperationHash) || String.IsNullOrEmpty(close.ContentHash) ||
                    Enumerable.Range(0, snapshot.PartHashes.Count).Any(number =>
                        !snapshot.PartHashes.ContainsKey(PartId(number))))
                {
                    throw new InvalidDataException("A restored close does not match its retained part metadata.");
                }

                var manifest = new DatabaseGenerationManifest(snapshot.PartHashes
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new DatabaseGenerationPart(item.Key, item.Value)).ToArray());
                if (manifest.Fingerprint != snapshot.ManifestFingerprint)
                {
                    throw new InvalidDataException("A restored revision has an inconsistent manifest.");
                }

                if (snapshot.Status is DatabaseStagedGenerationStatus.Sealed)
                {
                    ValidateClose(snapshot.Start!, snapshot.Pages, close);
                }

                break;
            default:
                throw new InvalidDataException("A restored revision has an unknown lifecycle status.");
        }
    }

    private void ReleaseTerminalPayloads(DatabaseGenerationScope scope)
    {
        foreach (DatabaseStagedGeneration generation in _catalog.Generations.Where(item => item.Scope == scope &&
                     item.Status is DatabaseStagedGenerationStatus.Published or
                         DatabaseStagedGenerationStatus.Retired))
        {
            Assembly assembly = AssemblyFor(generation.Identity.GenerationId);
            _assemblies[generation.Identity.GenerationId] = assembly with
            {
                Start = null,
                Pages = [],
                PayloadBytes = 0
            };
        }
    }

    private void ValidateSealAdmission(Guid generationId, SemanticChangeSessionClose close,
        DatabaseGenerationManifest manifest)
    {
        byte[] encodedClose = _codec.Serialize(close);
        if (encodedClose.Length > SemanticContributionRevisionLimits.FormatMaximumEmbeddedPartBytes)
        {
            throw new InvalidDataException("A contribution revision close exceeds the durable format bound.");
        }

        DatabaseStagedGeneration generation = _catalog.Get(generationId);
        Assembly assembly = AssemblyFor(generationId);
        long currentMetadata = MetadataBytes(assembly.Scope, generation.Identity, assembly.Start,
            assembly.Close, generation.ExpectedPublishedGeneration, generation.Parts,
            generation.ManifestFingerprint);
        long sealedMetadata = MetadataBytes(assembly.Scope, generation.Identity, assembly.Start,
            close, generation.ExpectedPublishedGeneration, generation.Parts, manifest.Fingerprint);
        long addedBytes = checked(sealedMetadata - currentMetadata);
        ValidateResourceEnvelope(checked(RevisionBytes(generation, assembly) + addedBytes),
            checked(TotalRevisionBytes() + addedBytes));
    }

    private void ValidateResourceEnvelope(long revisionBytes, long totalBytes)
    {
        if (revisionBytes > _limits.MaximumBytesPerRevision ||
            totalBytes > _limits.MaximumTotalRetainedBytes)
        {
            throw new InvalidDataException("Contribution revision staging exceeds its configured resource limits.");
        }
    }

    private long RevisionBytes(DatabaseStagedGeneration generation, Assembly assembly) => checked(
        assembly.PayloadBytes + MetadataBytes(assembly.Scope, generation.Identity, assembly.Start,
            assembly.Close, generation.ExpectedPublishedGeneration, generation.Parts,
            generation.ManifestFingerprint));

    private long MetadataBytes(SemanticContributionScope scope, DatabaseGenerationIdentity identity,
        SemanticChangeSessionStart? start, SemanticChangeSessionClose? close,
        DatabaseGenerationIdentity? expected, IReadOnlyDictionary<string, string> parts,
        string? manifestFingerprint)
    {
        long bytes = checked(StringBytes(scope.SourceSystem.AbsoluteUri) +
            StringBytes(scope.SourcePartition.AbsoluteUri) + 16 + StringBytes(identity.Revision) +
            StringBytes(identity.DefinitionHash));
        if (start is not null)
        {
            bytes = checked(bytes + _codec.Serialize(start).Length);
        }

        if (close is not null)
        {
            bytes = checked(bytes + _codec.Serialize(close).Length);
        }

        if (expected is not null)
        {
            bytes = checked(bytes + 16 + StringBytes(expected.Revision) + StringBytes(expected.DefinitionHash));
        }

        foreach (KeyValuePair<string, string> part in parts)
        {
            bytes = checked(bytes + StringBytes(part.Key) + StringBytes(part.Value));
        }

        if (manifestFingerprint is not null)
        {
            bytes = checked(bytes + StringBytes(manifestFingerprint));
        }

        return bytes;
    }

    private static long PartMetadataBytes(DatabaseGenerationPart part) => checked(
        StringBytes(part.PartId) + StringBytes(part.ContentHash));

    private static int StringBytes(string value) => checked(sizeof(int) + Encoding.UTF8.GetByteCount(value));

    private void ValidateSnapshotIdentity(SemanticContributionRevisionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot.Scope);
        ArgumentNullException.ThrowIfNull(snapshot.Identity);
        if (snapshot.Start is null)
        {
            return;
        }

        ValidateStart(snapshot.Start);
        if (Identity(snapshot.Start) != snapshot.Identity ||
            snapshot.Scope != new SemanticContributionScope(snapshot.Start.Source.SourceSystem,
                snapshot.Start.Source.SourcePartition))
        {
            throw new InvalidDataException("A restored contribution revision has inconsistent identity metadata.");
        }
    }

    private static DatabaseGenerationIdentity? ToKernel(SemanticContributionRevision? identity) =>
        identity is null ? null : new(identity.GenerationId, identity.Revision, identity.DefinitionHash);

    private static SemanticContributionRevision? FromKernel(DatabaseGenerationIdentity? identity) => identity is null
        ? null : new(identity.GenerationId, identity.Revision, identity.DefinitionHash);

    private static DatabaseGenerationScope Scope(SemanticChangeSessionSource source) => new(
        "semantic.contribution.v1", Field(source.SourceSystem.AbsoluteUri) + Field(source.SourcePartition.AbsoluteUri));

    private static DatabaseGenerationScope Scope(Uri sourceSystem, Uri sourcePartition) => new(
        "semantic.contribution.v1", Field(sourceSystem.AbsoluteUri) + Field(sourcePartition.AbsoluteUri));

    private static string Field(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";

    private static string PartId(int pageNumber) => $"page:{pageNumber:D10}";

    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
}
