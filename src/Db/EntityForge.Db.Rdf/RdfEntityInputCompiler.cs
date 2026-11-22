using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Compiles stable target slots through the same default-graph planner as mapped RDF POCO writes.</summary>
public sealed class RdfEntityInputCompiler
{
    /// <summary>Freezes a complete weakly typed entity through the same planner as mapped POCO writes.</summary>
    public RdfEntityWrite PrepareSave(IRdfDatabase database, Guid operationId, Entity entity,
        string? producerFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return PrepareReplacement(database, operationId, entity.Members, producerFingerprint);
    }

    private readonly Func<IRdfMappingSnapshot, IReadOnlyDictionary<string, object?>, DatabaseVersion, RdfEntityFluentExtensions.RdfEntityWriteSet> _replace;
    private readonly Func<IRdfMappingSnapshot, object, DatabaseVersion, RdfEntityFluentExtensions.RdfEntityWriteSet> _delete;
    private readonly Func<EntityCapturedRequest, RdfIri> _subject;

    private RdfEntityInputCompiler(EntityMappingManifest binding,
        Func<IRdfMappingSnapshot, IReadOnlyDictionary<string, object?>, DatabaseVersion, RdfEntityFluentExtensions.RdfEntityWriteSet> replace,
        Func<IRdfMappingSnapshot, object, DatabaseVersion, RdfEntityFluentExtensions.RdfEntityWriteSet> delete,
        Func<EntityCapturedRequest, RdfIri> subject)
    {
        Manifest = binding;
        _replace = replace;
        _delete = delete;
        _subject = subject;
    }

    internal async ValueTask<RdfEntityFluentExtensions.RdfEntityWriteSet> PrepareCapturedSetAsync(
        IRdfEntityReadScope view, EntityCapturedRequest request, CancellationToken cancellationToken)
    {
        RdfIri subject = _subject(request);
        var state = await view.ReadDefaultSubjectAsync(subject, cancellationToken).ConfigureAwait(false);
        return PrepareCapturedSet(new CapturedSubject(subject, state), request, view.Version);
    }

    internal RdfIri CapturedRoot(EntityCapturedRequest request) => _subject(request);

    private sealed class CapturedSubject(RdfIri subject, IReadOnlyList<RdfQuad> state) : IRdfMappingSnapshot
    {
        public IReadOnlyList<RdfQuad> ReadDefaultSubject(RdfIri requested) => requested == subject ? state :
            throw new InvalidOperationException("The planner requested an uncaptured subject.");
    }

    internal async ValueTask<RdfEntityWrite> PrepareCapturedAsync(IRdfDatabase database, EntityCapturedRequest request,
        CancellationToken cancellationToken)
    {
        var metadata = request.Description;
        using var snapshot = await database.AcquireSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var view = new LocalRdfMappingSnapshot(database, snapshot);
        var write = PrepareCapturedSet(view, request, snapshot.Version);
        return new RdfEntityWrite(Manifest, new(metadata.OperationId, write.Mutations, ReadVersion: write.ReadVersion, ReadPredicates: write.Reads,
            InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(Manifest, null, metadata.Delete)),
            metadata.DatabaseIncarnation, null, metadata.Delete);
    }

    internal RdfEntityFluentExtensions.RdfEntityWriteSet PrepareCapturedSet(IRdfMappingSnapshot view,
        EntityCapturedRequest request, DatabaseVersion version)
    {
        var metadata = request.Description;
        object value = request.Value();
        var write = metadata.Delete ? _delete(view, value, metadata.ExpectedVersion ?? version)
            : _replace(view, ((Entity)value).Members, metadata.ExpectedVersion ?? version);
        var reads = metadata.ExpectedSubjectState is null ? write.Reads : write.Reads.Cast<RdfSubjectStateRead>()
            .Select(read => (RdfReadPredicate)new RdfSubjectStateRead(read.Subject, read.Graph, read.MatchDefaultGraphOnly,
                metadata.ExpectedSubjectState)).ToArray();
        return write with
        {
            Reads = reads
        };
    }

    /// <summary>Gets the immutable logical slots, codecs and native target interpretation.</summary>
    public EntityMappingManifest Manifest
    {
        get;
    }

    /// <summary>Projects a POCO map into a native compiler without constructing the POCO.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public static RdfEntityInputCompiler FromMap<T>(RdfEntityMap<T> map, EntityModelIdentity model,
        PersistentEntityTypeIdentity entityType, string convention = "explicit-v1",
        IReadOnlyDictionary<string, string>? persistentMemberIdentities = null)
    {
        return FromBinding(map.ResolveBinding(model, entityType, convention, persistentMemberIdentities));
    }

    /// <summary>Compiles plain input using an already resolved mapping without reconstructing its identities.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public static RdfEntityInputCompiler FromBinding<T>(RdfResolvedEntityMapping<T> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return FromManifest(resolved.Manifest);
    }

    /// <summary>Reconstructs target execution without producer CLR types or accessors.</summary>
    public static RdfEntityInputCompiler FromManifest(EntityMappingManifest manifest)
    {
        var (native, shape) = RdfEntityTarget.Create(manifest);
        return new(manifest,
            (snapshot, values, version) => RdfEntityFluentExtensions.PrepareSaveEntity(snapshot, shape.Capture(values), native, version),
            (snapshot, identity, version) => RdfEntityFluentExtensions.PrepareDeleteEntity(snapshot, native, shape.CaptureIdentity(identity), version),
            request => request.Description.Delete
                ? RdfEntityMaterialization.Subject(native, shape.CaptureIdentity(request.Value()))
                : RdfEntityMaterialization.Subject(native, shape.Capture(((Entity)request.Value()).Members)));
    }


    /// <summary>Freezes complete replacement of mapped default-graph predicates. Named graphs remain untouched.</summary>
    public RdfEntityWrite PrepareReplacement(IRdfDatabase database, Guid operationId,
        IReadOnlyDictionary<string, object?> values, string? producerFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        using RdfSnapshotLease snapshot = database.AcquireSnapshot();
        var write = _replace(new LocalRdfMappingSnapshot(database, snapshot), values, snapshot.Version);
        return new RdfEntityWrite(Manifest, new(operationId, write.Mutations, ReadVersion: write.ReadVersion, ReadPredicates: write.Reads,
            InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(Manifest, producerFingerprint, false)),
            ((IEntityMappingReplayHost)database).DatabaseIncarnation, producerFingerprint, false);
    }

    /// <summary>Freezes removal of mapped default-graph state, preserving independent resources and unrelated predicates.</summary>
    public RdfEntityWrite PrepareDelete(IRdfDatabase database, Guid operationId, object identity,
        string? producerFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        using RdfSnapshotLease snapshot = database.AcquireSnapshot();
        var write = _delete(new LocalRdfMappingSnapshot(database, snapshot), identity, snapshot.Version);
        return new RdfEntityWrite(Manifest, new(operationId, write.Mutations, ReadVersion: write.ReadVersion, ReadPredicates: write.Reads,
            InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(Manifest, producerFingerprint, true)),
            ((IEntityMappingReplayHost)database).DatabaseIncarnation, producerFingerprint, true);
    }
}
