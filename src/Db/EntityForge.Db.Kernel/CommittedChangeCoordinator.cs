using EntityForge.Db.Durability;
using EntityForge.Db;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Kernel;

internal sealed class CommittedChangeCoordinator
{
    private readonly IDatabaseCommittedChangeStore _store;
    private readonly IDatabaseChangeCursorCodec _cursorCodec;
    private readonly Func<Guid> _incarnation;
    private readonly Func<DatabaseVersion> _durableVersion;

    public CommittedChangeCoordinator(IDatabaseCommittedChangeStore store,
        IDatabaseChangeCursorCodec cursorCodec, Func<Guid> incarnation,
        Func<DatabaseVersion> durableVersion)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _cursorCodec = cursorCodec ?? throw new ArgumentNullException(nameof(cursorCodec));
        _incarnation = incarnation ?? throw new ArgumentNullException(nameof(incarnation));
        _durableVersion = durableVersion ?? throw new ArgumentNullException(nameof(durableVersion));
    }

    public DatabaseChangeCursor Cursor(DatabaseVersion version) =>
        _cursorCodec.Encode(_incarnation(), version);

    public long Decode(DatabaseChangeCursor cursor) =>
        _cursorCodec.Decode(cursor, _incarnation(), _durableVersion()).Value;

    public ValueTask MaterializeAsync(DurableTransactionEnvelope transaction,
        CancellationToken cancellationToken) => transaction.CommittedChanges is null
        ? ValueTask.CompletedTask
        : _store.AppendAsync(ChangeSet(transaction), cancellationToken);

    public ValueTask PublishAsync(DatabaseVersion version, CancellationToken cancellationToken) =>
        _store.PublishThroughAsync(Cursor(version), cancellationToken);

    public ValueTask NotifyPublishedAsync(DatabaseVersion version, CancellationToken cancellationToken) =>
        _store.NotifyPublishedAsync(Cursor(version), cancellationToken);

    public ValueTask NotifyPublishedAsync(DatabaseChangeCursor cursor, CancellationToken cancellationToken) =>
        _store.NotifyPublishedAsync(cursor, cancellationToken);

    public async ValueTask NotifyRecoveredAsync(DatabaseChangeCursor cursor,
        CancellationToken cancellationToken)
    {
        try
        {
            await _store.NotifyPublishedAsync(cursor, cancellationToken).ConfigureAwait(false);
        }
        catch (EntityForgeChangeCursorInvalidException) when (_store is IDatabaseChangeJournalDiagnostics)
        {
            DatabaseChangeJournalStatus status = await ((IDatabaseChangeJournalDiagnostics)_store)
                .GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.RetentionFloor is not DatabaseChangeCursor floor || Decode(cursor) > Decode(floor) ||
                status.MaterializedCurrent is not DatabaseChangeCursor current)
            {
                throw;
            }

            await _store.NotifyPublishedAsync(current, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask RestorePublishedFrontierAsync(DatabaseChangeCursor? cursor,
        CancellationToken cancellationToken)
    {
        if (cursor is null)
        {
            return;
        }

        try
        {
            await _store.PublishThroughAsync(cursor.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (EntityForgeChangeCursorInvalidException exception)
        {
            if (_store is IDatabaseChangeJournalDiagnostics diagnostics)
            {
                DatabaseChangeJournalStatus status = await diagnostics.GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (status.RetentionFloor is DatabaseChangeCursor floor && Decode(cursor.Value) <= Decode(floor))
                {
                    return;
                }
            }
            throw new EntityForgeDbFormatException(
                "The committed-change journal is missing the checkpoint materialization boundary.", exception);
        }
    }

    public async ValueTask ValidateCheckpointCoverageAsync(DatabaseChangeCursor? checkpointCursor,
        CancellationToken cancellationToken)
    {
        DatabaseChangeFeedBounds bounds = await _store.GetBoundsAsync(cancellationToken).ConfigureAwait(false);
        if (bounds.Current is null)
        {
            if (checkpointCursor is not null)
            {
                throw new EntityForgeDbFormatException(
                    "The committed-change journal is missing the checkpoint materialization boundary.");
            }

            return;
        }
        if (checkpointCursor is not null)
        {
            long checkpointVersion = Decode(checkpointCursor.Value);
            if (Decode(bounds.Current.Value) < checkpointVersion)
            {
                throw new EntityForgeDbFormatException(
                    "The committed-change journal does not reach the checkpoint materialization boundary.");
            }

            if (_store is IDatabaseChangeJournalDiagnostics diagnostics)
            {
                DatabaseChangeJournalStatus status = await diagnostics.GetStatusAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (status.RetentionFloor is DatabaseChangeCursor floor && Decode(floor) >= checkpointVersion)
                {
                    return;
                }
            }
        }
    }

    private CommittedChangeSet ChangeSet(DurableTransactionEnvelope transaction) => new(
        _incarnation(), transaction.Version, transaction.TransactionId, transaction.Payload.Profile,
        Cursor(transaction.Version), transaction.CommittedChanges!);
}
