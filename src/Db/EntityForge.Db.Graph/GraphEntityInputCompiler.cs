using EntityForge.Db.Mapping;
using EntityForge.Db.Graph.Contracts;

namespace EntityForge.Db.Graph;

/// <summary>Compiles stable target-slot values through the same native planner as mapped Graph POCO writes.</summary>
public sealed class GraphEntityInputCompiler
{
    /// <summary>Freezes a complete weakly typed entity through the same planner as mapped POCO writes.</summary>
    public GraphEntityWrite PrepareSave(IGraphDatabase database, Guid operationId, Entity entity,
        string? producerFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return PrepareReplacement(database, operationId, entity.Members, producerFingerprint);
    }

    private readonly Func<IGraphMappingSnapshot, IReadOnlyDictionary<string, object?>, DatabaseVersion, Guid, GraphEntityFluentExtensions.GraphEntityWriteSet> _replace;
    private readonly Func<IGraphMappingSnapshot, object, DatabaseVersion, GraphEntityFluentExtensions.GraphEntityWriteSet> _delete;
    private readonly GraphEntityMap<IReadOnlyDictionary<string, object?>> _native;
    private readonly EntityRecordShape<IReadOnlyDictionary<string, object?>> _shape;

    private GraphEntityInputCompiler(EntityMappingManifest binding,
        GraphEntityMap<IReadOnlyDictionary<string, object?>> native,
        EntityRecordShape<IReadOnlyDictionary<string, object?>> shape,
        Func<IGraphMappingSnapshot, IReadOnlyDictionary<string, object?>, DatabaseVersion, Guid, GraphEntityFluentExtensions.GraphEntityWriteSet> replace,
        Func<IGraphMappingSnapshot, object, DatabaseVersion, GraphEntityFluentExtensions.GraphEntityWriteSet> delete)
    {
        Manifest = binding;
        _native = native;
        _shape = shape;
        _replace = replace;
        _delete = delete;
    }

    internal GraphEntityWrite PrepareCaptured(IGraphDatabase database, EntityCapturedRequest request)
    {
        var metadata = request.Description;
        using var snapshot = database.AcquireSnapshot();
        var view = new LocalGraphMappingSnapshot(database, snapshot.Version);
        var write = PrepareCapturedSet(view, request, snapshot.Version);
        return new GraphEntityWrite(Manifest, new(metadata.OperationId, write.Mutations, write.ReadVersion, write.Reads,
            InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(Manifest, null, metadata.Delete)),
            metadata.DatabaseIncarnation, null, metadata.Delete);
    }

    internal GraphEntityFluentExtensions.GraphEntityWriteSet PrepareCapturedSet(IGraphMappingSnapshot view,
        EntityCapturedRequest request, DatabaseVersion version)
    {
        var metadata = request.Description;
        object value = RestoreCapturedValue(request);
        return metadata.Delete ? _delete(view, value, metadata.ExpectedVersion ?? version)
            : _replace(view, ((Entity)value).Members, metadata.ExpectedVersion ?? version, metadata.OperationId);
    }

    internal async ValueTask<GraphEntityWrite> PrepareCapturedAsync(IGraphDatabase database,
        EntityCapturedRequest request, CancellationToken cancellationToken)
    {
        await using var view = new EmbeddedGraphEntityReadScope(database,
            await database.AcquireSnapshotAsync(cancellationToken: cancellationToken).ConfigureAwait(false));
        var write = await PrepareCapturedSetAsync(view, request, cancellationToken).ConfigureAwait(false);
        var metadata = request.Description;
        return new GraphEntityWrite(Manifest, new(metadata.OperationId, write.Mutations, write.ReadVersion, write.Reads,
            InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(Manifest, null, metadata.Delete)),
            metadata.DatabaseIncarnation, null, metadata.Delete);
    }

    // The caller owns admission, the retained scope and global/local version translation.
    internal ValueTask<GraphEntityFluentExtensions.GraphEntityWriteSet> PrepareCapturedSetAsync(
        IGraphEntityReadScope view, EntityCapturedRequest request, CancellationToken cancellationToken = default)
        => PrepareCapturedSetAsync(new GraphEntityPlanningScope(view), request, cancellationToken);

    internal ValueTask<GraphEntityFluentExtensions.GraphEntityWriteSet> PrepareCapturedSetAsync(
        IGraphEntityPlanningScope view, EntityCapturedRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (native, shape) = (_native, _shape);
        object value = RestoreCapturedValue(request);
        DatabaseVersion version = request.Description.ExpectedVersion ?? view.Version;
        return request.Description.Delete
            ? GraphEntityFluentExtensions.PrepareDeleteEntityAsync(view, shape.CaptureIdentity(value), native, version, cancellationToken)
            : GraphEntityFluentExtensions.PrepareSaveEntityAsync(view, shape.Capture(((Entity)value).Members), native, version, cancellationToken, request.Description.OperationId);
    }

    internal GraphVertexId CapturedRoot(EntityCapturedRequest request)
    {
        var (native, shape) = (_native, _shape);
        object value = RestoreCapturedValue(request);
        object captured = request.Description.Delete ? shape.CaptureIdentity(value) : shape.Capture(((Entity)value).Members);
        var member = native.EntityMap.Identity!;
        return GraphEntityMaterialization.VertexId(EntityMappedValueValidation.Validate(member,
            request.Description.Delete ? captured : member.Read(captured))!, native.VertexLabel);
    }

    private object RestoreCapturedValue(EntityCapturedRequest request)
    {
        object value = request.Value();
        if (value is Entity entity)
        {
            value = new Entity(entity.Members.ToDictionary(pair => pair.Key, pair =>
                Manifest.Members.Single(member => member.Identity == pair.Key).ValueCodec == "entityforge/graph-vertex-id"
                    ? RestoreGraphIds(pair.Value) : pair.Value, StringComparer.Ordinal));
        }

        return value;
    }

    /// <summary>Gets the immutable logical slots, codecs and native target interpretation.</summary>
    public EntityMappingManifest Manifest
    {
        get;
    }

    /// <summary>Projects an explicit POCO map into a compiler that never constructs the POCO.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public static GraphEntityInputCompiler FromMap<T>(GraphEntityMap<T> map, EntityModelIdentity model,
        PersistentEntityTypeIdentity entityType, string convention = "explicit-v1",
        IReadOnlyDictionary<string, string>? persistentMemberIdentities = null)
    {
        return FromBinding(map.ResolveBinding(model, entityType, convention, persistentMemberIdentities));
    }

    /// <summary>Compiles plain input using an already resolved mapping without reconstructing its identities.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public static GraphEntityInputCompiler FromBinding<T>(GraphResolvedEntityMapping<T> resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        return FromManifest(resolved.Manifest);
    }

    /// <summary>Reconstructs target execution without producer CLR types or accessors.</summary>
    public static GraphEntityInputCompiler FromManifest(EntityMappingManifest manifest)
    {
        var (native, shape) = GraphEntityTarget.Create(manifest);
        return new(manifest, native, shape,
            (snapshot, values, version, seed) => GraphEntityFluentExtensions.PrepareSaveEntity(snapshot, shape.Capture(values), native, version, seed),
            (snapshot, identity, version) => GraphEntityFluentExtensions.PrepareDeleteEntity(snapshot, shape.CaptureIdentity(identity), native, version));
    }

    private static object? RestoreGraphIds(object? value) => value switch
    {
        Guid id => new GraphVertexId(id),
        EntityCollection collection => new EntityCollection(collection.Kind, collection.Items.Select(RestoreGraphIds).ToArray()),
        _ => value
    };



    /// <summary>Freezes one complete-record replacement. Omitted nullable slots are removed; unrelated native slots remain.</summary>
    public GraphEntityWrite PrepareReplacement(IGraphDatabase database, Guid operationId,
        IReadOnlyDictionary<string, object?> values, string? producerFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        using GraphSnapshotLease snapshot = database.AcquireSnapshot();
        var write = _replace(new LocalGraphMappingSnapshot(database, snapshot.Version), values, snapshot.Version, operationId);
        return new GraphEntityWrite(Manifest, new(operationId, write.Mutations, write.ReadVersion, write.Reads,
            InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(Manifest, producerFingerprint, false)),
            ((IEntityMappingReplayHost)database).DatabaseIncarnation, producerFingerprint, false);
    }

    /// <summary>Freezes removal of map-owned state while preserving independent vertices and unrelated slots.</summary>
    public GraphEntityWrite PrepareDelete(IGraphDatabase database, Guid operationId, object identity,
        string? producerFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        using GraphSnapshotLease snapshot = database.AcquireSnapshot();
        var write = _delete(new LocalGraphMappingSnapshot(database, snapshot.Version), identity, snapshot.Version);
        return new GraphEntityWrite(Manifest, new(operationId, write.Mutations, write.ReadVersion, write.Reads,
            InterpretationFingerprint: EntityRecordInterpretation.Fingerprint(Manifest, producerFingerprint, true)),
            ((IEntityMappingReplayHost)database).DatabaseIncarnation, producerFingerprint, true);
    }
}
