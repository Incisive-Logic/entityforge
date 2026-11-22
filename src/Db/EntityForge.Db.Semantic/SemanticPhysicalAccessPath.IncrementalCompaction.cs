using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Storage;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticPhysicalAccessPath
{
    // The bound includes input reads and complete output batches. Device stalls can
    // still exceed a time deadline; retained database size no longer dictates one slice.
    private const int CheckpointCompactionPageBudget = 2048;
    private CheckpointCompaction? _checkpointCompaction;
    private Guid? _checkpointCompactionPublication;

    public bool HasPendingCheckpointCompaction => _checkpointCompaction is not null;

    public bool CanAdvancePrivateCheckpointCompaction => _checkpointCompaction is { BaseReady: false };

    public async ValueTask<bool> AdvanceCheckpointCompactionAsync(DatabaseVersion version,
        Func<IReadOnlyList<DatabasePhysicalPageMutation>, CancellationToken, ValueTask> journal,
        CancellationToken token, int pageBudget = CheckpointCompactionPageBudget)
    {
        if (_checkpointCompaction is null)
        {
            _checkpointCompaction = new CheckpointCompaction(this, version);
            _telemetry.CheckpointCompaction("started");
            _checkpointCompaction.Start();
        }
        var work = _checkpointCompaction;
        work.Journal = journal;
        // Private base construction also advances between checkpoints. Final
        // catch-up runs only at checkpoint admission, so applying the background
        // time cap there would let intervening writes outrun every catch-up pass.
        // Keep its page cap and atomic publication boundary unchanged.
        try
        {
            bool ready = await work.Work.AdvanceAsync(pageBudget, token,
                maximumDuration: work.BaseReady ? null : TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            _telemetry.CheckpointCompaction(ready ? "ready" : "yielded");
            return ready;
        }
        finally
        {
            work.Journal = null;
        }
    }

    public async ValueTask StopCheckpointCompactionAsync()
    {
        var work = _checkpointCompaction;
        _checkpointCompaction = null;
        _checkpointCompactionPublication = null;
        if (work is null)
        {
            return;
        }

        try
        {
            await work.Work.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            work.Release();
            _telemetry.CheckpointCompaction("abandoned");
        }
    }

    private async ValueTask<OrderedPageTreeStage?> PrepareIncrementalCompactionPublicationAsync(Guid id, CancellationToken token)
    {
        var work = _checkpointCompaction ?? throw new InvalidOperationException("No private compaction is available.");
        if (!work.Work.IsCompleted)
        {
            throw new InvalidOperationException("Private compaction is not ready for publication.");
        }

        var stage = await PrepareReplacementBatchAsync(id, work.Replacements, token).ConfigureAwait(false);
        // Membership comes from records actually retained in the base plus
        // committed delta writes, never from conservative protection marks.
        // Abandoned writes can mark an atom after its base record was filtered.
        // A cold/stale cache still requires the full canonical/duplicate check
        // on the published directory. Do not let compaction bypass that check.
        if (_atomAdmission is { } current && current.Generation == _shared.Generation)
        {
            _stagedAtomAdmission.Add(id, current.Identifiers.Intersect(work.RetainedAtoms));
        }

        if (_publishedAtomSequence is { } sequence && sequence.Generation == _shared.Generation)
        {
            _stagedUnchangedAtomSequences.Add(id, sequence);
        }

        _checkpointCompactionPublication = id;
        return stage;
    }

    private void PublishedIncrementalCompaction(Guid id)
    {
        if (_checkpointCompactionPublication != id)
        {
            return;
        }

        _checkpointCompactionPublication = null;
        var completed = _checkpointCompaction!;
        _checkpointCompaction = null;
        completed.Release();
        _telemetry.CheckpointCompaction("published");
    }

    private sealed class CheckpointCompaction
    {
        private readonly SemanticPhysicalAccessPath _owner;
        private readonly DatabaseVersion _floor;
        private readonly PhysicalMaintenancePageStore _pages;
        private readonly IDisposable[] _pins;
        private bool _released;

        public bool BaseReady
        {
            get; private set;
        }

        public System.Collections.Immutable.ImmutableHashSet<Guid>.Builder RetainedAtoms
        {
            get;
        }
        =
            System.Collections.Immutable.ImmutableHashSet.CreateBuilder<Guid>();

        public PhysicalMaintenanceWork Work { get; } = new();

        public List<(PhysicalMaintenanceTarget Target, Guid ReplacementId)> Replacements { get; } = [];

        public SemanticRetentionReferences ProtectedReferences { get; } = new();

        public Func<IReadOnlyList<DatabasePhysicalPageMutation>, CancellationToken, ValueTask>? Journal
        {
            get; set;
        }

        public CheckpointCompaction(SemanticPhysicalAccessPath owner, DatabaseVersion floor)
        {
            _owner = owner;
            _floor = floor;
            _pages = (PhysicalMaintenancePageStore)owner._pages;
            _pins = [owner._ranges.PinReplacementSource(),
                owner._shared.PinReplacementSource(),
                owner._concepts.PinReplacementSource(),
                .. owner._fields.PinReplacementSources(),
                .. owner._relationships.PinReplacementSources()];
        }

        public void Protect(SemanticAtomId atom, long ordinal)
        {
            ProtectedReferences.AddAtom(atom.Value);
            ProtectedReferences.AddOrdinal(ordinal);
        }

        public IDisposable EnterWorkspace(PhysicalPageWorkspace workspace) => _pages.UseWorkspace(workspace);

        public void Start() => Work.Start(async token =>
        {
            using var scope = _pages.Enter(Work, async (mutations, cancellation) =>
            {
                await (Journal ?? throw new InvalidOperationException("Maintenance writes escaped their admitted slice."))(mutations, cancellation).ConfigureAwait(false);
                _owner._telemetry.CheckpointCompaction("journaled_pages", mutations.Count);
            });
            var retain = await _owner.CaptureRetentionFilterAsync(_floor, token, orderedDirectoryPass: true).ConfigureAwait(false);
            _owner._telemetry.CheckpointCompaction("reference_scan_completed");
            foreach (var target in _owner.GetVacuumTargets())
            {
                Guid replacement = Guid.NewGuid();
                bool RetainAndRecord(SemanticAccessKey key, byte[] value)
                {
                    if (!retain(key, value))
                    {
                        return false;
                    }

                    if (key.Family == SemanticAccessFamily.AtomDirectory)
                    {
                        if (key.Value.Length != 0 || !SemanticRetentionReferences.TryReadAtom(key.Term, out var atom))
                        {
                            throw new InvalidDataException("Invalid retained atom directory identity.");
                        }

                        _ = DecodeAtomDirectory(value);
                        RetainedAtoms.Add(atom);
                    }
                    return true;
                }
                await _owner.BuildAppendReplacementAsync(target, replacement, _floor, token,
                    target == PhysicalMaintenanceTarget.Shared ? RetainAndRecord : retain, pruneEvidence: true).ConfigureAwait(false);
                Replacements.Add((target, replacement));
                _owner._telemetry.CheckpointCompaction("base_target_completed");
            }
            // Release admission after building the base before reconciling writes.
            // This also bounds a slice that finishes a large final replacement.
            BaseReady = true;
            await Work.YieldAsync().ConfigureAwait(false);
            // Catch-up may itself span slices. Pinned source runs cannot disappear,
            // so each completed catch-up advances the private base even if writers
            // appended more runs while this task was paused.
            do
            {
                foreach (var replacement in Replacements)
                {
                    await _owner.CatchUpReplacementAsync(replacement.Target, replacement.ReplacementId, _floor, token).ConfigureAwait(false);
                }
            } while (!Current());
        });

        private bool Current()
        {
            var directory = _owner._pageSpace.CaptureDirectory();
            return Replacements.All(replacement =>
            {
                var metadata = _owner.ReplacementMetadata(replacement.Target, replacement.ReplacementId);
                return directory.Get(metadata.Id).Generation == metadata.Generation;
            });
        }

        public void Release()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            foreach (var replacement in Replacements)
            {
                _owner.AbandonAppendReplacement(replacement.ReplacementId);
            }

            foreach (var pin in _pins)
            {
                pin.Dispose();
            }
        }
    }
}
