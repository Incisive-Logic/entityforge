using EntityForge.Semantics;
using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Semantic;

internal sealed record SemanticSourceMetadataImage(IReadOnlyList<CheckpointSourceMembership> Memberships,
    IReadOnlyList<CheckpointSourcePartitionProgress> Progress);

internal interface ISemanticSourceMetadataSnapshot
{
    ValueTask<SemanticSourceMetadataImage> ReadSourceMetadataAsync(CancellationToken token = default);
}

internal sealed partial class SemanticPhysicalAccessPath
{
    private readonly SemanticSourceMetadataCodec _sourceMetadata = new();

    private sealed class SourceMetadataValueComparer : IAppendMergeKeyedValueComparer<SemanticAccessKey, byte[]>
    {
        internal static readonly SourceMetadataValueComparer Instance = new();

        public int Compare(byte[]? left, byte[]? right) => ByteArrayComparer.Instance.Compare(left!, right!);

        public int Compare(SemanticAccessKey key, byte[] left, byte[] right) =>
            key.Family is SemanticAccessFamily.SourceMembership or SemanticAccessFamily.SourceProgress
                ? 0 : Compare(left, right);
    }

    internal SemanticSourceMetadataReference SourceMetadataReference(DatabaseVersion version, int memberships, int progress) =>
        new(SharedAccessPathId.Value, _shared.Generation, version, memberships, progress);

    internal void ValidateSourceMetadataReference(SemanticSourceMetadataReference reference)
    {
        if (reference.AccessPathId != SharedAccessPathId.Value || _shared.Generation < reference.MinimumGeneration)
        {
            throw new InvalidDataException("Checkpoint source metadata access path is absent or older than its captured root.");
        }
    }


    private IEnumerable<SemanticAtomRecord> MetadataAtoms(SemanticSourceMetadataChanges? changes)
    {
        if (changes is null)
        {
            yield break;
        }

        foreach (var change in changes.Memberships)
        {
            if (change.After is { } membership)
            {
                yield return Atom(membership.Subject);
                yield return Atom(membership.Provenance);
                yield return Atom(new AbsoluteUriSemanticValue(membership.SourcePartition));
            }
        }
    }

    private SemanticProjectionPlan WithSourceMetadata(SemanticProjectionPlan plan,
        SemanticSourceMetadataChanges? changes, CancellationToken token)
    {
        if (changes is null || changes.Memberships.Count == 0 && changes.Progress.Count == 0)
        {
            return plan;
        }

        var mutations = new List<AppendMergeMutation<SemanticAccessKey, byte[]>>();
        foreach (var change in changes.Memberships)
        {
            token.ThrowIfCancellationRequested();
            if (change.Before is { } before)
            {
                mutations.AddRange(_sourceMetadata.Membership(before, AppendMergeMutationKind.Remove));
            }

            if (change.After is { } after)
            {
                mutations.AddRange(_sourceMetadata.Membership(after, AppendMergeMutationKind.Add, Atom));
            }
        }
        foreach (var change in changes.Progress)
        {
            token.ThrowIfCancellationRequested();
            if (change.Before is { } before)
            {
                mutations.AddRange(_sourceMetadata.Progress(before, AppendMergeMutationKind.Remove));
            }

            if (change.After is { } after)
            {
                mutations.AddRange(_sourceMetadata.Progress(after, AppendMergeMutationKind.Add));
            }
        }
        return plan with
        {
            SharedMutations = plan.SharedMutations.Concat(mutations).ToArray()
        };
    }

    internal async ValueTask<SemanticSourceMetadataImage> ReadSourceMetadataAsync(long maximumVersion,
        CancellationToken token)
    {
        using var snapshot = _shared.CaptureSnapshot(maximumVersion: maximumVersion);
        return await ReadSourceMetadataAsync(snapshot, token).ConfigureAwait(false);
    }

    private async ValueTask<SemanticSourceMetadataImage> ReadSourceMetadataAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot, CancellationToken token)
    {
        var stored = new List<(SemanticAccessKey Key, SemanticSourceMetadataCodec.MembershipReferences References)>();
        var ids = new HashSet<SemanticAtomId>();
        await foreach (var row in snapshot.ReadVisibleAsync(RecoveryFamily(SemanticAccessFamily.SourceMembership), token))
        {
            if (row.Key.Value.Length != 0)
            {
                throw new InvalidDataException("Invalid membership reference key.");
            }

            var refs = _sourceMetadata.DecodeMembershipReferences(row.Value);
            ids.Add(refs.Subject);
            ids.Add(refs.Provenance);
            ids.Add(refs.Partition);
            stored.Add((row.Key, refs));
        }
        // Resolve from this exact snapshot without a cache bypassing missing/corrupt roots.
        var atoms = await ReadRecoveryAtomsAsync(snapshot, ids, token).ConfigureAwait(false);
        var decoded = new SemanticDecodedAtomScope(atoms);
        var memberships = new List<CheckpointSourceMembership>(stored.Count);
        foreach (var row in stored)
        {
            token.ThrowIfCancellationRequested();
            var membership = _sourceMetadata.ResolveMembership(row.References, decoded);
            _sourceMetadata.ValidateMembershipKey(membership, row.Key.Term);
            memberships.Add(membership);
        }
        var progress = new List<CheckpointSourcePartitionProgress>();
        await foreach (var value in _sourceMetadata.ReadProgressAsync(snapshot, token))
        {
            progress.Add(value);
        }

        return new(memberships.ToArray(), progress.ToArray());
    }
}
