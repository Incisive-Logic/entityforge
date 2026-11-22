using System.Collections.Immutable;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticPhysicalAccessPath
{
    // Derived from the complete validated current directory, never from a query cache.
    // Only identifiers are retained; payloads and historical reads remain on disk.
    private sealed record AtomAdmission(long Generation, ImmutableHashSet<Guid> Identifiers);

    private AtomAdmission? _atomAdmission;
    private readonly Dictionary<Guid, ImmutableHashSet<Guid>> _stagedAtomAdmission = [];
    private readonly Dictionary<Guid, (CheckpointCompaction Work, Guid[] Atoms)> _stagedCompactionAtoms = [];

    internal long AtomAdmissionRebuilds
    {
        get; private set;
    }

    internal int AtomAdmissionCount => _atomAdmission?.Identifiers.Count ?? 0;

    private async ValueTask<ImmutableHashSet<Guid>> ReadAtomAdmissionAsync(
        AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_atomAdmission is { } current && current.Generation == snapshot.Generation)
        {
            return current.Identifiers;
        }

        var identifiers = ImmutableHashSet.CreateBuilder<Guid>();
        await foreach (var entry in snapshot.ReadVisibleAsync(
            RecoveryFamily(SemanticAccessFamily.AtomDirectory), token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(entry.Key.Term);
            }
            catch (FormatException failure)
            {
                throw new InvalidDataException("Invalid atom directory identity.", failure);
            }
            if (bytes.Length != GuidByteLength || entry.Key.Value.Length != 0)
            {
                throw new InvalidDataException("Invalid atom directory identity.");
            }

            var id = new Guid(bytes);
            if (GuidKey(id) != entry.Key.Term || !identifiers.Add(id))
            {
                throw new InvalidDataException("Atom directory contains a noncanonical or duplicate identity.");
            }

            _ = DecodeAtomDirectory(entry.Value);
        }
        token.ThrowIfCancellationRequested();
        var complete = identifiers.ToImmutable();
        _atomAdmission = new(snapshot.Generation, complete);
        AtomAdmissionRebuilds++;
        return complete;
    }

    private void CarryAtomAdmission(Guid transactionId)
    {
        if (!_stagedAtomAdmission.ContainsKey(transactionId) &&
            _atomAdmission is { } current && current.Generation == _shared.Generation)
        {
            _stagedAtomAdmission.Add(transactionId, current.Identifiers);
        }
    }
}
