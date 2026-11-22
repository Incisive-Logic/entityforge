using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.AccessPaths;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Rdf;

internal sealed class RdfPhysicalAccessPath
{
    public bool RequiresCheckpointMaintenance => _pageSpace.RequiresCheckpointMaintenance;

    public void AuthorizeCheckpointPageReuse() => _pageSpace.AuthorizeCheckpointPageReuse();

    private static readonly PhysicalAccessPathId AccessPathId =
        new(new Guid("34c98858-00e7-48c4-aeb9-f587efdbf93b"));

    private static readonly PhysicalAccessMethodKind AccessMethod = new("append-merge.rdf-ordered", 2);
    private readonly AppendMergeAccessPath<string, byte[]> _access;
    private readonly PhysicalPageSpace _pageSpace;
    private readonly IResizablePhysicalPageStore _pages;
    private readonly HashSet<Guid> _staged = [];
    private readonly Dictionary<Guid, IReadOnlyList<DatabasePhysicalPageMutation>> _stagedMutations = [];
    private readonly Dictionary<Guid, PhysicalPageWorkspace> _replacements = [];

    private RdfPhysicalAccessPath(AppendMergeAccessPath<string, byte[]> access,
        PhysicalPageSpace pageSpace, IResizablePhysicalPageStore pages)
    {
        _access = access;
        _pageSpace = pageSpace;
        _pages = pages;
    }

    public static async ValueTask<RdfPhysicalAccessPath> OpenAsync(IResizablePhysicalPageStore pages,
        PhysicalPageSpace pageSpace, CancellationToken cancellationToken = default)
    {
        PhysicalAccessPathDirectoryEntry? published = pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
        AppendMergeAccessPath<string, byte[]> access = await AppendMergeAccessPath<string, byte[]>.OpenAsync(
            pages, AccessPathId, AccessMethod, published, Utf8Codec.Instance, ByteArrayCodec.Instance,
            StringComparer.Ordinal, ByteArrayComparer.Instance, options: new(compactOnWrite: true), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new(access, pageSpace, pages);
    }

    internal static AppendMergeMutation<string, byte[]>[] Compile(RdfTransaction transaction) =>
        transaction.Mutations.SelectMany(Mutations).ToArray();

    public ValueTask<OrderedPageTreeStage?> PrepareAsync(Guid transactionId, long databaseVersion,
        RdfTransaction transaction, CancellationToken cancellationToken) =>
        PrepareCompiledAsync(transactionId, databaseVersion, Compile(transaction), cancellationToken);

    internal async ValueTask<OrderedPageTreeStage?> PrepareCompiledAsync(Guid transactionId, long databaseVersion,
        AppendMergeMutation<string, byte[]>[] mutations, CancellationToken cancellationToken)
    {
        if (mutations.Length == 0)
        {
            return null;
        }

        AppendMergeMutationPlan<string, byte[]> plan = _access.PlanMutations(
            mutations, databaseVersion, cancellationToken);
        return await PreparePlanAsync(transactionId, plan, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<OrderedPageTreeStage?> PrepareGroupAsync(Guid transactionId,
        IReadOnlyList<(long Version, AppendMergeMutation<string, byte[]>[] Mutations)> members, CancellationToken token)
    {
        var plans = members.Select(member => _access.PlanMutations(
            member.Mutations, member.Version, token)).ToArray();
        return PreparePlanAsync(transactionId, _access.PlanMutationGroup(plans), token);
    }

    private async ValueTask<OrderedPageTreeStage?> PreparePlanAsync(Guid transactionId,
        AppendMergeMutationPlan<string, byte[]> plan, CancellationToken cancellationToken)
    {
        if (plan.Count == 0)
        {
            return null;
        }

        PhysicalPageSpaceTransaction physical = _pageSpace.BeginTransaction(transactionId);
        try
        {
            PhysicalAccessPathDirectoryEntry? current = _pageSpace.CaptureDirectory().Entries
                .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
            AppendMergeStage stage = await _access.PrepareAsync(transactionId, plan,
                physical.OpenAccessPath(AccessPathId, AccessMethod, current?.Generation ?? -1),
                cancellationToken).ConfigureAwait(false);
            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(
                physical, cancellationToken).ConfigureAwait(false);
            _staged.Add(transactionId);
            _stagedMutations.Add(transactionId, stage.PhysicalMutations);
            return new(transactionId, new(stage.Generation),
                stage.PhysicalMutations.Concat(directory.PhysicalMutations).ToArray());
        }
        catch
        {
            _access.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            throw;
        }
    }

    public ValueTask PersistAsync(Guid transactionId, CancellationToken cancellationToken) =>
        _pageSpace.PersistAsync(transactionId, _stagedMutations[transactionId], cancellationToken);

    public ValueTask FlushAsync(CancellationToken cancellationToken) => _pages.FlushAsync(cancellationToken);

    public void Publish(Guid transactionId)
    {
        if (_staged.Remove(transactionId))
        {
            _access.Publish(transactionId);
        }

        _pageSpace.Publish(transactionId);
        _stagedMutations.Remove(transactionId);
    }

    public void Abandon(Guid transactionId)
    {
        if (_staged.Remove(transactionId))
        {
            _access.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
        }
        _stagedMutations.Remove(transactionId);
    }

    public RdfPhysicalSnapshot CaptureSnapshot() => new(_access.CaptureSnapshot());

    public async ValueTask<IReadOnlyList<DatabasePhysicalPageMutation>> PrepareMaintenanceAsync(Guid id,
        long version, bool compact, CancellationToken token)
    {
        var physical = _pageSpace.BeginVacuumTransaction(id);
        try
        {
            PhysicalAccessPathDirectoryEntry? current = _pageSpace.CaptureDirectory().Entries
                .SingleOrDefault(entry => entry.AccessPathId == AccessPathId);
            var allocation = physical.OpenAccessPath(AccessPathId, AccessMethod, current?.Generation ?? -1);
            var stage = compact
                ? await _access.PrepareVacuumAsync(id, version, allocation, token).ConfigureAwait(false)
                : await _access.PrepareMaintenanceAsync(id, version, allocation, token).ConfigureAwait(false);
            if (stage is null)
            {
                _pageSpace.Abandon(id);
                return [];
            }
            var directory = await _pageSpace.PrepareAsync(physical, token).ConfigureAwait(false);
            _staged.Add(id);
            _stagedMutations.Add(id, stage.Stage.PhysicalMutations);
            return stage.Stage.PhysicalMutations.Concat(directory.PhysicalMutations).ToArray();
        }
        catch
        {
            _access.Abandon(id);
            _pageSpace.Abandon(id);
            throw;
        }
    }

    public ValueTask TrimAsync(CancellationToken token) => _pageSpace.TrimUnownedTailAsync(token);

    public async ValueTask<RdfPhysicalQueryPage> ReadPageAsync(RdfPhysicalSnapshot snapshot,
        RdfQuadPattern pattern, int pageSize, AppendMergeCursor<string, byte[]>? cursor,
        CancellationToken cancellationToken)
    {
        string? exact = ExactKey(pattern);
        OrderedIndexRange<string> range = exact is null
            ? OrderedIndexRange<string>.Between("q:", "q:\uffff")
            : OrderedIndexRange<string>.Between(exact, exact);
        var results = new List<RdfQuad>(pageSize);
        AppendMergeCursor<string, byte[]>? position = cursor;
        while (results.Count < pageSize)
        {
            AppendMergePage<string, byte[]> page = await snapshot.Inner.ReadPageAsync(
                pageSize - results.Count, range, position, cancellationToken).ConfigureAwait(false);
            byte[][] values = page.Items.Select(item => item.Value).ToArray();
            results.AddRange(await ResolvePageAsync(snapshot, pattern, exact, values, cancellationToken).ConfigureAwait(false));
            position = page.NextCursor;
            if (position is null)
            {
                break;
            }
        }
        return new(results, position);
    }

    // One forward merge per request; page boundaries do not restart every run reader.
    internal async IAsyncEnumerable<(IReadOnlyList<RdfQuad> Quads, bool Complete)> ReadScanPagesAsync(
        RdfPhysicalSnapshot snapshot, RdfQuadPattern pattern, int pageSize, int maximumExaminedQuads,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, Action? chargeCandidate = null)
    {
        string? exact = ExactKey(pattern);
        var range = exact is null ? OrderedIndexRange<string>.Between("q:", "q:\uffff") :
            OrderedIndexRange<string>.Between(exact, exact);
        var values = new List<byte[]>(pageSize);
        long examined = 0;
        await foreach (var entry in snapshot.Inner.ReadVisibleAsync(range, cancellationToken).ConfigureAwait(false))
        {
            chargeCandidate?.Invoke();
            if (++examined > maximumExaminedQuads)
            {
                throw new RdfSparqlWorkLimitException("SPARQL scan exceeded its examined-quad budget before pattern filtering.");
            }
            values.Add(entry.Value);
            if (values.Count == pageSize)
            {
                yield return (await ResolvePageAsync(snapshot, pattern, exact, values.ToArray(), cancellationToken).ConfigureAwait(false), false);
                values.Clear();
            }
        }
        yield return (await ResolvePageAsync(snapshot, pattern, exact, values.ToArray(), cancellationToken).ConfigureAwait(false), true);
    }

    private static async ValueTask<IReadOnlyList<RdfQuad>> ResolvePageAsync(RdfPhysicalSnapshot snapshot,
        RdfQuadPattern pattern, string? exact, byte[][] values, CancellationToken cancellationToken)
    {
        var results = new List<RdfQuad>(values.Length);
        IReadOnlyDictionary<string, IReadOnlyList<byte[]>>? resolved = null;
        string[]? references = null;
        if (exact is not null && values.Length != 0)
        {
            references = values.Select(Convert.ToBase64String).ToArray();
            string[] keys = references.Select(QuadKey).Distinct(StringComparer.Ordinal).ToArray();
            resolved = await snapshot.Inner.FindManyAsync(keys, cancellationToken).ConfigureAwait(false);
            foreach (string key in keys)
            {
                int count = resolved.GetValueOrDefault(key, []).Count;
                if (count == 0)
                {
                    throw new InvalidDataException($"RDF quad directory entry '{key[2..]}' is missing.");
                }

                if (count != 1)
                {
                    throw new InvalidDataException(
                        $"RDF quad content identifier '{key[2..]}' is not unique.");
                }
            }
        }
        for (int index = 0; index < values.Length; index++)
        {
            RdfQuad quad = RdfPhysicalCodec.DecodeQuad(exact is null
                ? values[index] : resolved![QuadKey(references![index])][0]);
            if (Matches(quad, pattern))
            {
                results.Add(quad);
            }
        }
        return results;
    }

    public async ValueTask BuildReplacementAsync(Guid replacementId, IEnumerable<RdfQuad> quads,
        long databaseVersion, CancellationToken cancellationToken)
    {
        AppendMergeMutation<string, byte[]>[] mutations = quads.SelectMany(Entries)
            .Select(entry => new AppendMergeMutation<string, byte[]>(AppendMergeMutationKind.Add,
                entry.Key, entry.Value)).ToArray();
        PhysicalPageWorkspace workspace = _pageSpace.BeginReplacementWorkspace(replacementId);
        try
        {
            await _access.BuildReplacementAsync(replacementId, mutations, Math.Max(1, databaseVersion),
                workspace, cancellationToken: cancellationToken).ConfigureAwait(false);
            _replacements.Add(replacementId, workspace);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public async ValueTask<OrderedPageTreeStage> PrepareReplacementAsync(Guid transactionId,
        Guid replacementId, CancellationToken cancellationToken)
    {
        PhysicalPageWorkspace workspace = _replacements.GetValueOrDefault(replacementId) ??
            throw new AppendMergeException("The RDF append/merge replacement is unavailable.");
        long expected = _access.GetReplacementExpectedGeneration(replacementId);
        long expectedDirectoryGeneration = _pageSpace.CaptureDirectory().Entries
            .SingleOrDefault(entry => entry.AccessPathId == AccessPathId)?.Generation ?? -1;
        PhysicalPageSpaceTransaction physical = _pageSpace.BeginTransaction(transactionId);
        try
        {
            physical.OpenReplacementAccessPath(AccessPathId, AccessMethod,
                expectedDirectoryGeneration, workspace);
            AppendMergeStage stage = _access.PrepareReplacement(transactionId, replacementId);
            PhysicalPageSpacePersistenceStage directory = await _pageSpace.PrepareAsync(
                physical, cancellationToken).ConfigureAwait(false);
            _replacements.Remove(replacementId);
            _staged.Add(transactionId);
            _stagedMutations.Add(transactionId, stage.PhysicalMutations);
            return new(transactionId, new(stage.Generation), directory.PhysicalMutations);
        }
        catch
        {
            _access.Abandon(transactionId);
            _pageSpace.Abandon(transactionId);
            _replacements.Remove(replacementId);
            workspace.Dispose();
            long actual = _access.Generation;
            if (actual != expected)
            {
                throw new AppendMergeReplacementStaleException(expected, actual);
            }

            throw;
        }
    }

    public long Generation => _access.Generation;

    public long GetReplacementExpectedGeneration(Guid replacementId) =>
        _access.GetReplacementExpectedGeneration(replacementId);

    public void AbandonReplacement(Guid replacementId)
    {
        _access.AbandonReplacement(replacementId);
        if (_replacements.Remove(replacementId, out var workspace))
        {
            workspace.Dispose();
        }
    }

    private static IEnumerable<AppendMergeMutation<string, byte[]>> Mutations(RdfMutation mutation)
    {
        AppendMergeMutationKind kind = mutation is AssertRdfQuad
            ? AppendMergeMutationKind.Add : AppendMergeMutationKind.Remove;
        foreach ((string key, byte[] value) in Entries(mutation.Quad))
        {
            yield return new(kind, key, value);
        }
    }

    private static IEnumerable<KeyValuePair<string, byte[]>> Entries(RdfQuad quad)
    {
        byte[] encoded = RdfPhysicalCodec.EncodeQuad(quad);
        byte[] referenceBytes = ContentHash(encoded);
        string reference = Convert.ToBase64String(referenceBytes);
        yield return new(QuadKey(reference), encoded);
        yield return new(Key('s', quad.Subject), referenceBytes);
        yield return new(Key('p', quad.Predicate), referenceBytes);
        yield return new(Key('o', quad.Object), referenceBytes);
        yield return new(quad.Graph is null ? "d:" : Key('g', quad.Graph), referenceBytes);
    }

    private static string Key(char family, RdfTerm term)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(RdfPhysicalCodec.EncodeTerm(term), digest);
        // Preserve the persisted 128-bit hash and padded base64 key format,
        // without a temporary truncated byte array or base64 string per key.
        Span<char> key = stackalloc char[26];
        key[0] = family;
        key[1] = ':';
        Convert.TryToBase64Chars(digest[..16], key[2..], out _);
        return new string(key);
    }

    private static string QuadKey(string reference) => "q:" + reference;

    private static byte[] ContentHash(byte[] encoded)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(encoded, digest);
        return digest[..16].ToArray();
    }

    private static string? ExactKey(RdfQuadPattern pattern) => SelectedFamily(pattern) switch
    {
        RdfPhysicalAccessFamily.Subject => Key('s', pattern.Subject!),
        RdfPhysicalAccessFamily.Object => Key('o', pattern.Object!),
        RdfPhysicalAccessFamily.Predicate => Key('p', pattern.Predicate!),
        RdfPhysicalAccessFamily.Graph => Key('g', pattern.Graph!),
        RdfPhysicalAccessFamily.DefaultGraph => "d:",
        _ => null
    };

    internal static RdfPhysicalAccessFamily SelectedFamily(RdfQuadPattern pattern) =>
        pattern.Subject is not null ? RdfPhysicalAccessFamily.Subject :
        pattern.Object is not null ? RdfPhysicalAccessFamily.Object :
        pattern.Predicate is not null ? RdfPhysicalAccessFamily.Predicate :
        pattern.Graph is not null ? RdfPhysicalAccessFamily.Graph :
        pattern.MatchDefaultGraphOnly ? RdfPhysicalAccessFamily.DefaultGraph :
        RdfPhysicalAccessFamily.FullScan;

    private static bool Matches(RdfQuad quad, RdfQuadPattern pattern) =>
        (pattern.Subject is null || Equals(pattern.Subject, quad.Subject)) &&
        (pattern.Predicate is null || Equals(pattern.Predicate, quad.Predicate)) &&
        (pattern.Object is null || Equals(pattern.Object, quad.Object)) &&
        (pattern.Graph is null || Equals(pattern.Graph, quad.Graph)) &&
        (!pattern.MatchDefaultGraphOnly || quad.Graph is null);

    private sealed class Utf8Codec : IOrderedIndexCodec<string>
    {
        public static Utf8Codec Instance { get; } = new();

        public byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

        public string Decode(ReadOnlySpan<byte> value) => Encoding.UTF8.GetString(value);
    }

    private sealed class ByteArrayCodec : IValidatingOrderedIndexCodec<byte[]>
    {
        public static ByteArrayCodec Instance { get; } = new();

        public byte[] Encode(byte[] value) => value;

        public byte[] Decode(ReadOnlySpan<byte> value) => value.ToArray();

        public void Validate(ReadOnlySpan<byte> value)
        {
            // This codec accepts arbitrary bytes, just like Decode. The run codec
            // still validates record headers and bounds; RDF decoding validates
            // selected quad payloads. Unselected values need no array allocation.
        }
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? left, byte[]? right) =>
            (left, right) switch
            {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                _ => left.AsSpan().SequenceCompareTo(right)
            };
    }
}

internal sealed class RdfPhysicalSnapshot(AppendMergeSnapshot<string, byte[]> inner) : IDisposable
{
    internal AppendMergeSnapshot<string, byte[]> Inner { get; } = inner;

    public void Dispose() => Inner.Dispose();
}

internal enum RdfPhysicalAccessFamily
{
    Subject, Object, Predicate, Graph, DefaultGraph, FullScan
}

internal sealed record RdfPhysicalQueryPage(IReadOnlyList<RdfQuad> Quads,
    AppendMergeCursor<string, byte[]>? NextCursor);
