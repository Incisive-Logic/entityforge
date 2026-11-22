using EntityForge.Db.Storage.Pages;
using EntityForge.Db.AccessPaths;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticPhysicalAccessPath
{
    internal long RetentionOrdinalScans
    {
        get; private set;
    }

    // Capture source roots under drained checkpoint admission. An incremental scan keeps these
    // immutable snapshots across slices; later writes protect their own atom references.
    // No admitted snapshot can observe ended rows
    // below this boundary. Atom payloads are self-contained canonical values, not references to atoms.
    private async ValueTask<Func<SemanticAccessKey, byte[], bool>> CaptureRetentionFilterAsync(
        DatabaseVersion floor, CancellationToken token, bool orderedDirectoryPass = false)
    {
        RetentionReferenceScans++;
        // Capture every root before the first budgeted read can yield to writers.
        using var shared = _shared.CaptureSnapshot(pinExactPages: true);
        using var concepts = _concepts.CaptureSnapshot(pinExactPages: true);
        using var ranges = _ranges.CaptureSnapshot(pinExactPages: true);
        using var fields = _fields.CaptureSnapshot(pinExactPages: true);
        var incremental = _checkpointCompaction;
        const int EncodedAtomLength = 24;
        var references = new SemanticRetentionReferences();
        bool RetainAssertion(byte[] value, SemanticAssertionRowKind kind)
        {
            var row = SemanticAssertionRowCodec.DecodeReferences(value, kind);
            if (row.EndVersion is { } end && end.Value <= floor.Value)
            {
                return false;
            }

            references.AddAtom(row.First.Value);
            references.AddAtom(row.Second.Value);
            references.AddAtom(row.Third.Value);
            references.AddAtom(row.Fourth.Value);
            return true;
        }
        bool RetainRow(SemanticAccessKey key, byte[] value) => key.Family switch
        {
            SemanticAccessFamily.RelationshipAssertion => RetainAssertion(value, SemanticAssertionRowKind.Relationship),
            SemanticAccessFamily.FieldAssertion => RetainAssertion(value, SemanticAssertionRowKind.Field),
            _ => true
        };
        void MarkKey(string key)
        {
            if (key.Length == 0 || key.Length % EncodedAtomLength != 0)
            {
                return;
            }

            for (int offset = 0; offset < key.Length; offset += EncodedAtomLength)
            {
                if (SemanticRetentionReferences.TryReadAtom(key.AsSpan(offset, EncodedAtomLength), out var atom))
                {
                    references.AddAtom(atom);
                }
            }
        }
        async Task ScanAsync(AppendMergeSnapshot<SemanticAccessKey, byte[]> snapshot,
            OrderedIndexRange<SemanticAccessKey> range, Action<SemanticAccessKey, byte[]> visit)
        {
            await foreach (var item in snapshot.ReadVisibleAsync(range, token).ConfigureAwait(false))
            {
                visit(item.Key, item.Value);
            }
        }
        // A current-state scan would miss provenance referenced by older retained
        // membership versions. Conservatively trace every stored addition; later
        // compaction removes obsolete references before their atoms become eligible.
        await foreach (var item in shared.ReadStoredAdditionsAsync(RecoveryFamily(SemanticAccessFamily.SourceMembership), token))
        {
            var refs = _sourceMetadata.DecodeMembershipReferences(item.Value);
            references.AddAtom(refs.Subject.Value);
            references.AddAtom(refs.Provenance.Value);
            references.AddAtom(refs.Partition.Value);
        }
        await foreach (var item in fields.ReadVisibleEvidenceAsync(token).ConfigureAwait(false))
        {
            RetainAssertion(item.Value, SemanticAssertionRowKind.Field);
        }
        void TraceReferences(SemanticAccessKey key, byte[] value)
        {
            if (key.Family is SemanticAccessFamily.AtomDirectory or SemanticAccessFamily.AtomPayload or SemanticAccessFamily.AtomSequence)
            {
                return;
            }

            if (!RetainRow(key, value))
            {
                return;
            }

            MarkKey(key.Term);
            MarkKey(key.Value);
        }
        // Payloads are canonical values, not outgoing references. Restrict the merge
        // readers before decoding pages instead of hydrating the entire atom store
        // and discarding it in the visitor. Inclusive upper sentinels have empty keys;
        // the visitor also excludes them if present.
        await ScanAsync(shared, OrderedIndexRange<SemanticAccessKey>.Through(
            new(SemanticAccessFamily.AtomDirectory, string.Empty, string.Empty)), TraceReferences).ConfigureAwait(false);
        await ScanAsync(shared, OrderedIndexRange<SemanticAccessKey>.Between(
            new(SemanticAccessFamily.OutgoingRelationship, string.Empty, string.Empty),
            new(SemanticAccessFamily.AtomPayload, string.Empty, string.Empty)), TraceReferences).ConfigureAwait(false);
        await ScanAsync(shared, OrderedIndexRange<SemanticAccessKey>.From(
            new(SemanticAccessFamily.EntityStringOrder, string.Empty, string.Empty)), TraceReferences).ConfigureAwait(false);
        foreach (var access in new[] { concepts, ranges })
        {
            await ScanAsync(access, OrderedIndexRange<SemanticAccessKey>.All, TraceReferences).ConfigureAwait(false);
        }

        if (!orderedDirectoryPass)
        {
            RetentionOrdinalScans++;
            await ScanAsync(shared, RecoveryFamily(SemanticAccessFamily.AtomDirectory), (key, value) =>
            {
                if (key.Family == SemanticAccessFamily.AtomDirectory && references.ContainsAtom(key.Term))
                {
                    references.AddOrdinal(DecodeAtomDirectory(value).Ordinal);
                }
            }).ConfigureAwait(false);
        }
        bool RetainDirectory(SemanticAccessKey key, byte[] value)
        {
            bool keep = references.ContainsAtom(key.Term) || incremental?.ProtectedReferences.ContainsAtom(key.Term) == true;
            // A complete ordered replacement visits all directory records (family 7)
            // before payload records (family 11). Derive ordinals from that pass
            // rather than decoding the entire atom directory a second time.
            // Interleaved writes protect their ordinals separately; catch-up keeps
            // their actual committed records. Other maintenance retains its scan.
            if (keep && orderedDirectoryPass)
            {
                references.AddOrdinal(DecodeAtomDirectory(value).Ordinal);
            }

            return keep;
        }
        return (key, value) => key.Family switch
        {
            SemanticAccessFamily.AtomDirectory => RetainDirectory(key, value),
            SemanticAccessFamily.AtomPayload => references.ContainsOrdinal(key.Term) || incremental?.ProtectedReferences.ContainsOrdinal(key.Term) == true,
            _ => RetainRow(key, value)
        };
    }

    public void ClearMaintenanceCaches()
    {
        _atomCache.Clear();
        _atomKeyCache.Clear();
        _decodedIdentityCache.Clear();
        _relationshipRowCache.Clear();
        _relationshipEvidenceCache.Clear();
        _fieldEvidenceCache.Clear();
        _relationshipAdjacencyCache.Clear();
        _relationshipAssertionCache.Clear();
        _fieldCandidateCache.Clear();
        _fieldAssertionCache.Clear();
        _identityMembershipCache.Clear();
    }
}
