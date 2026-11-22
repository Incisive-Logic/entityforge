using EntityForge.Db.Kernel;
using EntityForge.Db.Semantic.Durability;

namespace EntityForge.Db.Semantic;

internal sealed partial class SemanticDurabilityRuntime : IDatabaseProfileLogicalPreparation, IDatabaseProfileSnapshotPin
{
    internal Func<CancellationToken, ValueTask<IDisposable?>>? SnapshotAdmission
    {
        get; set;
    }

    public ValueTask<IDisposable?> EnterSnapshotPinAsync(CancellationToken token) =>
        (SnapshotAdmission ?? throw new InvalidOperationException("Snapshot maintenance admission is missing."))(token);

    public IDisposable PinSnapshot(DatabaseVersion version) => Participant().PinSnapshot(version);

    private readonly DatabaseLogicalPreparationCache<DurableDatabaseCommit> _logicalIntents = new();

    public void RetainLogicalPreparation(Guid operation, ReadOnlyMemory<byte> payload) =>
        _logicalIntents.RetainCompiled(operation, payload, owned => DecodeMeasured(DatabaseVersion.Initial, owned.Span));

    public void CaptureLogicalEffects(Guid operation, ReadOnlyMemory<byte> payload)
    {
        // The planner retains exact effects plus source dependency evidence.
        // Changed or unsupported dependencies fall back to full planning.
        Participant().CaptureLogicalEffects(operation);
    }

    public void ReleaseLogicalPreparation(Guid operation)
    {
        _logicalIntents.Remove(operation);
        Participant().ReleaseLogicalEffects(operation);
    }

    public void ClearLogicalPreparations()
    {
        _logicalIntents.Clear();
        Participant().ReleaseLogicalEffects(null);
    }

    private DurableDatabaseCommit DecodeLogicalCommit(Guid operation, DatabaseVersion version, ReadOnlyMemory<byte> payload) =>
        _logicalIntents.TryGet(operation, payload.Span, out var retained)
            ? retained with
            {
                Version = version
            } : DecodeMeasured(version, payload.Span);

    private DurableDatabaseCommit DecodeMeasured(DatabaseVersion version, ReadOnlySpan<byte> payload)
    {
        long started = DatabaseProfileWorkTelemetry.Start();
        var commit = _decoder.Decode(version, payload);
        if (DatabaseProfileWorkTelemetry.IsEnabled && commit.Kind == DurableDatabaseCommitKind.SemanticTransaction)
        {
            DatabaseProfileWorkTelemetry.Record("semantic", "decode", "entity-change",
                commit.Contributions.Sum(c => c.Session.Pages.Sum(p => p.Changes.Count)), started);
        }

        return commit;
    }
}
