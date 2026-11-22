using System.Runtime.CompilerServices;
using EntityForge.Db;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Kernel;

internal sealed class DatabaseChangeFeed
{
    private readonly IDatabaseCommittedChangeStore _store;
    private readonly CommittedChangeCoordinator _changes;
    private readonly DatabaseCommitCoordinator _commits;
    private readonly Action _ensureReady;
    private readonly DatabaseKernelTelemetry _telemetry;
    private readonly DatabaseProfileId _profile;

    public DatabaseChangeFeed(IDatabaseCommittedChangeStore store, CommittedChangeCoordinator changes,
        DatabaseCommitCoordinator commits, Action ensureReady, DatabaseKernelTelemetry telemetry,
        DatabaseProfileId profile)
    {
        _store = store;
        _changes = changes;
        _commits = commits;
        _ensureReady = ensureReady;
        _telemetry = telemetry;
        _profile = profile;
    }

    public async ValueTask<DatabaseChangeFeedBounds> GetBoundsAsync(CancellationToken cancellationToken)
    {
        await _commits.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ensureReady();
            return await _store.GetBoundsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commits.Release();
        }
    }

    public async ValueTask<DatabaseChangePage> ReadPageAfterAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, int maximumCount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (maximumCount <= 0 || maximumCount > DatabaseChangeFeedLimits.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        await _commits.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ensureReady();
            if (cursor is not null)
            {
                _ = _changes.Decode(cursor.Value);
            }

            DatabaseChangePage page = await _store.ReadPageAfterAsync(cursor, selector, maximumCount,
                cancellationToken).ConfigureAwait(false);
            _telemetry.ChangeFeedRead(_profile, page.Changes.Count);
            return page;
        }
        finally
        {
            _commits.Release();
        }
    }

    public async IAsyncEnumerable<CommittedChangeSet> SubscribeAsync(DatabaseChangeCursor? cursor,
        DatabaseChangeSelector selector, DatabaseChangeSubscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(options);
        _ensureReady();
        if (cursor is not null)
        {
            _ = _changes.Decode(cursor.Value);
        }

        await foreach (CommittedChangeSet change in _store.SubscribeAsync(cursor, selector, options,
                           cancellationToken).ConfigureAwait(false))
        {
            await _commits.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _ensureReady();
            }
            finally
            {
                _commits.Release();
            }
            _telemetry.ChangeFeedRead(_profile, 1);
            yield return change;
        }
    }
}
