using static EntityForge.Db.Semantic.SemanticEntityPlan;
using System.Text;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>Bounds a complete local aggregate operation; exceeded limits fail the operation.</summary>
public sealed record SemanticEntityStoreLimits
{
    /// <summary>Maximum entities in one prepared aggregate or retained-snapshot load.</summary>
    public const int MaximumEntities = 1_000;

    /// <summary>Maximum object-link depth, excluding the root.</summary>
    public const int MaximumDepth = 16;

    /// <summary>Maximum encoded session bytes or loaded scalar binary-size budget.</summary>
    public const int MaximumBytes = 64 * 1024 * 1024;

    /// <summary>Creates bounds no greater than the ordinary aggregate format limits.</summary>
    public SemanticEntityStoreLimits(int entities = MaximumEntities, int depth = MaximumDepth, int bytes = MaximumBytes)
    {
        if (entities is < 1 or > MaximumEntities || depth is < 0 or > MaximumDepth || bytes is < 1 or > MaximumBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(entities), "Aggregate limits exceed the supported ordinary-operation bounds.");
        }

        Entities = entities;
        Depth = depth;
        Bytes = bytes;
    }

    /// <summary>Gets the entity limit.</summary>
    public int Entities
    {
        get;
    }

    /// <summary>Gets the relationship-depth limit.</summary>
    public int Depth
    {
        get;
    }

    /// <summary>Gets the byte limit.</summary>
    public int Bytes
    {
        get;
    }
}

/// <summary>Stores bounded owned object trees and non-cascading references through native Semantic sessions.</summary>
public sealed partial class SemanticEntityStore<T> where T : class
{
    private static readonly TimeSpan SnapshotDuration = TimeSpan.FromMinutes(5);
    private readonly ISemanticEntityClient _client;
    private readonly SemanticEntityPlan _plan;
    private readonly SemanticApplicationIdentity _application;
    private readonly SemanticEntityStoreLimits _limits;

    private PlanNode _root => _plan.Root;

    private IReadOnlyList<EntityMappingManifest> _manifests => _plan.Manifests;

    private IReadOnlyList<SemanticTargetBinding> _targetBindings => _plan.TargetBindings;

    internal Func<ValueTask>? AfterRootRead
    {
        get; set;
    }

    private Uri Partition(SemanticEntityIdentity root) => _plan.Partition(root);

    internal SemanticEntityStore(ISemanticEntityClient client, SemanticAggregateMap<T> map,
        SemanticApplicationIdentity application, SemanticEntityStoreLimits limits, SemanticTargetBinding? rootBinding = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _application = application;
        _limits = limits;
        _plan = new(map.Node, application, limits, rootBinding);
    }

    /// <summary>Creates a producer-independent input compiler using this store's exact mapping and lifecycle planner.</summary>
    public SemanticEntityInputCompiler CreateInputCompiler() => new(_plan, _client.DatabaseIncarnation);

    /// <summary>Gets every durable contract required by this aggregate mapping closure.</summary>
    public IReadOnlyList<EntityMappingManifest> Manifests => _manifests;

    /// <summary>Gets the exact root and owned-slot target bindings recorded in aggregate provenance.</summary>
    public IReadOnlyList<SemanticTargetBinding> TargetBindings => _targetBindings;

    /// <summary>Saves a complete entity and its owned state. Ordinary saves use last-writer-wins upsert semantics.</summary>
    public ValueTask<SessionCommitResult> SaveAsync(T entity, CancellationToken cancellationToken = default) =>
        SaveAsync(entity, SemanticEntityWriteOperation.Create(), cancellationToken: cancellationToken);

    /// <summary>Saves with a stable operation identity. Reusing it requires identical content and observation time.</summary>
    public ValueTask<SessionCommitResult> SaveAsync(T entity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null, CancellationToken cancellationToken = default) => _plan.CaptureSaveCore(_client.DatabaseIncarnation, entity, operation, guard?.Predecessor, guard is not null).DeliverAsync(_client, cancellationToken);

    /// <summary>Deletes the application's root and owned state, preserving independent references.</summary>
    public ValueTask<SessionCommitResult> DeleteAsync(object identity, CancellationToken cancellationToken = default) =>
        DeleteAsync(identity, SemanticEntityWriteOperation.Create(), cancellationToken: cancellationToken);

    /// <summary>Deletes by CLR identity using an explicit replay operation.</summary>
    public ValueTask<SessionCommitResult> DeleteAsync(object identity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null, CancellationToken cancellationToken = default) => _plan.CaptureDeleteCore(_client.DatabaseIncarnation, identity, operation, guard?.Predecessor, guard is not null).DeliverAsync(_client, cancellationToken);

    /// <summary>Deletes an entity by its mapped identity.</summary>
    public ValueTask<SessionCommitResult> DeleteAsync(T entity, CancellationToken cancellationToken = default) =>
        DeleteAsync(entity, SemanticEntityWriteOperation.Create(), cancellationToken: cancellationToken);

    /// <summary>Deletes an entity by its mapped identity and explicit replay operation.</summary>
    public ValueTask<SessionCommitResult> DeleteAsync(T entity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return _plan.CaptureDeleteCore(_client.DatabaseIncarnation, _root.Node.CaptureKey(entity), operation, guard?.Predecessor, guard is not null).DeliverAsync(_client, cancellationToken);
    }

    /// <summary>Loads an entity and its configured owned/reference closure, or null when absent.</summary>
    public async ValueTask<T?> FindAsync(object identity, CancellationToken cancellationToken = default) =>
        (await ReadOnlyAsync(identity, cancellationToken).ConfigureAwait(false))?.Value;

    /// <summary>Loads a consistent snapshot without a contribution precondition for later writes.</summary>
    public async ValueTask<SemanticEntityReadSnapshot<T>?> ReadOnlyAsync(object identity, CancellationToken cancellationToken = default)
    {
        var result = await ReadCoreAsync(identity, cancellationToken, includeGuard: false).ConfigureAwait(false);
        return result is null ? null : new(result.Value, result.Version);
    }

    /// <summary>Prepares a complete owned tree. Omitted guard is unguarded; use Initial or After(position) explicitly.</summary>
    public SemanticEntityWrite PrepareSave(T entity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null) => _plan.PrepareSave(_client.DatabaseIncarnation, entity, operation, guard?.Predecessor, guard is not null);

    /// <summary>Captures locally and asks the receiving client to prepare a detached native write.</summary>
    public ValueTask<SemanticEntityWrite> PrepareSaveAsync(T entity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null, CancellationToken cancellationToken = default) =>
        _client.PrepareAsync(CaptureSave(entity, operation, guard), cancellationToken);

    /// <summary>Prepares deletion of the root contribution and every owned child; references are not deleted.</summary>
    public SemanticEntityWrite PrepareDelete(object identity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null) => _plan.PrepareDeleteCore(_client.DatabaseIncarnation, identity, operation, guard?.Predecessor, guard is not null);

    /// <summary>Asks the receiving client to prepare deletion with the explicit contribution guard.</summary>
    public ValueTask<SemanticEntityWrite> PrepareDeleteAsync(object identity, SemanticEntityWriteOperation operation,
        SemanticSourceProgressPrecondition? guard = null, CancellationToken cancellationToken = default) =>
        _client.PrepareAsync(CaptureDelete(identity, operation, guard), cancellationToken);

    /// <summary>Loads the entire configured closure from one retained snapshot, preserving reference identity.</summary>
    public async ValueTask<SemanticEntityReadResult<T>?> ReadAsync(object identity, CancellationToken cancellationToken = default)
    {
        var result = await ReadCoreAsync(identity, cancellationToken, includeGuard: true).ConfigureAwait(false);
        return result is null ? null : new(result.Value, result.Version, result.Guard!);
    }

    private sealed record Loaded(T Value, DatabaseVersion Version, SemanticSourceProgressPrecondition? Guard);

    internal (EntityMappingManifest[] Manifests, EntityReadRecipe Recipe) DescribePartitionRead(object identity) =>
        (_manifests.ToArray(), ReadRecipe(identity) with
        {
            IncludeContributionGuard = false
        });

    internal async ValueTask<T?> ReadPartitionValueAsync(object identity, ISemanticEntityReadScope scope, CancellationToken token)
    {
        var result = await ReadCoreAsync(identity, token, false, scope).ConfigureAwait(false);
        return result is null ? default : result.Value;
    }

    private async ValueTask<Loaded?> ReadCoreAsync(object identity, CancellationToken cancellationToken, bool includeGuard, ISemanticEntityReadScope? supplied = null)
    {
        var recipe = ReadRecipe(identity) with
        {
            IncludeContributionGuard = includeGuard
        };
        ISemanticEntityReadScope lease = supplied ?? (_client is IEntityCompleteReadClient complete
            ? (ISemanticEntityReadScope)await complete.ReadCompleteAsync(_manifests, recipe, _application.Identifier,
                Partition(_root.Node.IdentityValue(identity)), SnapshotDuration, cancellationToken).ConfigureAwait(false)
            : _client is IEntityReadRecipeProvider targeted
            ? (ISemanticEntityReadScope)await targeted.OpenRecipeAsync(_manifests, recipe, _application.Identifier,
                Partition(_root.Node.IdentityValue(identity)), SnapshotDuration, cancellationToken).ConfigureAwait(false)
            : await _client.OpenReadAsync(_manifests, _application.Identifier,
                Partition(_root.Node.IdentityValue(identity)), SnapshotDuration, cancellationToken).ConfigureAwait(false));
        return await EntityReadScopeLifetime.RunAsync<Loaded?>(lease, async () =>
        {
            if (lease is IEntityReadRecipeScope prefetched)
            {
                await prefetched.PrefetchRecipeAsync(recipe, cancellationToken).ConfigureAwait(false);
            }

            DatabaseVersion snapshot = lease.Version;
            var instances = new Dictionary<SemanticEntityIdentity, (PlanNode Plan, object Value)>();
            var materializedInstances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            long bytes = 0;
            async ValueTask<object?> Load(PlanNode plan, SemanticEntityIdentity key, int depth, bool required)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Entry chains may approach the entity limit without increasing domain depth.
                // Break synchronous ValueTask recursion so their length cannot exhaust the CLR stack.
                if (plan.Node.DepthCost == 0)
                {
                    await Task.Yield();
                }

                if (instances.TryGetValue(key, out var existing))
                {
                    if (existing.Plan != plan)
                    {
                        throw new InvalidOperationException("One identity has incompatible aggregate mappings.");
                    }

                    return existing.Value;
                }
                if (depth > _limits.Depth || instances.Count == _limits.Entities)
                {
                    throw new InvalidOperationException("The aggregate load exceeds its depth or entity limit.");
                }

                var projection = new EntityProjectionDefinition(plan.Node.ScalarProjection.Concept,
                    plan.Node.ScalarProjection.Fields.Append(new EntityFieldProjection(KeyField, KeyFieldName)));
                EntityProjectionProjectionResult result = await lease.ProjectAsync(new(key, projection), cancellationToken).ConfigureAwait(false);
                EntityProjectionNode? node = result.Document.Roots.SingleOrDefault();
                if (node is null)
                {
                    if (required)
                    {
                        throw new InvalidOperationException("An aggregate relationship target is missing at the retained snapshot.");
                    }

                    return null;
                }
                foreach (EntityProjectionValue value in node.Fields.SelectMany(field => field.Values))
                {
                    bytes = checked(bytes + SemanticLoadedValueSize.Count(value.Value));
                }

                bytes = checked(bytes + Encoding.UTF8.GetByteCount(Key(key)));
                if (bytes > _limits.Bytes)
                {
                    throw new InvalidOperationException("The aggregate load exceeds its byte limit.");
                }

                EntityProjectionValue[] keys = node.Fields.Single(field => field.Name == KeyFieldName).Values.ToArray();
                if (keys.Length != 1 || keys[0].Value is not LiteralValue original)
                {
                    throw new InvalidOperationException("The stored entity has no unique entity-store key. Native contributions or an older development format require explicit import.");
                }

                object valueObject = plan.Node.Materialize(node, original.LexicalValue);
                SemanticEntityIdentity materializedIdentity = plan.Node.ReadIdentity(valueObject);
                if (!StringComparer.Ordinal.Equals(SemanticValueConverter.ToLexical(materializedIdentity.Value), original.LexicalValue))
                {
                    throw new InvalidOperationException("The materialized key differs from the stored original aggregate key.");
                }

                if (key.Scheme != OwnedScheme && materializedIdentity != key)
                {
                    throw new InvalidOperationException("The materialized key differs from the retained entity identity.");
                }

                if (!materializedInstances.Add(valueObject))
                {
                    throw new InvalidOperationException("An aggregate materializer reused an instance for distinct entity identities.");
                }

                instances.Add(key, (plan, valueObject));
                if (depth == 0 && AfterRootRead is not null)
                {
                    await AfterRootRead().ConfigureAwait(false);
                }
                // Scalar maps do not consume relationships. Do not fetch or charge unrelated
                // native adjacency when there are no mapped links to materialize.
                IReadOnlyList<SemanticRelationshipEvidence> outgoing = plan.Links.Count == 0 ? [] :
                    await lease.TraverseAsync(new(key, SemanticRelationshipDirection.Outgoing), cancellationToken)
                        .ConfigureAwait(false);
                foreach (SemanticRelationshipEvidence edge in outgoing)
                {
                    bytes = checked(bytes + Encoding.UTF8.GetByteCount(Key(edge.Target)) +
                        Encoding.UTF8.GetByteCount(edge.Relationship.Identifier.AbsoluteUri));
                }

                if (bytes > _limits.Bytes)
                {
                    throw new InvalidOperationException("The aggregate load exceeds its byte limit.");
                }

                foreach (PlanLink link in plan.Links)
                {
                    SemanticEntityIdentity[] targets = outgoing.Where(edge => edge.Relationship == link.Link.Term).Select(edge => edge.Target).Distinct().ToArray();
                    if (targets.Length > 1 || targets.Length == 0 && !link.Link.Nullable)
                    {
                        throw new InvalidOperationException("An aggregate single-object link is missing or ambiguous.");
                    }

                    if (targets.Length == 1 && link.Link.Cascades && targets[0] != OwnedIdentity(key, link))
                    {
                        throw new InvalidOperationException("An owned link escapes its parent ownership slot.");
                    }

                    if (targets.Length == 1 && !link.Link.Cascades &&
                        (targets[0].Scheme != link.Target.Node.Scheme || targets[0].Concept != link.Target.Node.ScalarProjection.Concept))
                    {
                        throw new InvalidOperationException("A reference differs from its independent target binding.");
                    }

                    object? child = targets.Length == 0 ? null : await Load(link.Target, targets[0], depth + link.Target.Node.DepthCost, true).ConfigureAwait(false);
                    link.Link.Assign(valueObject, child);
                }
                return valueObject;
            }
            object? value = await Load(_root, _root.Node.IdentityValue(identity), 0, false).ConfigureAwait(false);
            return value is null ? null : new((T)value, snapshot, !includeGuard ? null :
                lease.ContributionGuard is null ? SemanticSourceProgressPrecondition.Initial : SemanticSourceProgressPrecondition.After(lease.ContributionGuard));
        }, () => lease.ValidateAsync(cancellationToken)).ConfigureAwait(false);
    }

    private EntityReadRecipe ReadRecipe(object identity)
    {
        var nodes = new Dictionary<PlanNode, int>(ReferenceEqualityComparer.Instance);
        var shapes = new List<EntityReadShape>();
        int Add(PlanNode node)
        {
            if (nodes.TryGetValue(node, out int existing))
            {
                return existing;
            }

            if (nodes.Count == EntityReadRecipe.MaximumShapes)
            {
                throw new InvalidOperationException("The complete Semantic read has too many types.");
            }

            int index = shapes.Count;
            nodes.Add(node, index);
            var fields = EntityReadRecipe.Value(node.Node.ScalarProjection.Fields.Append(new EntityFieldProjection(KeyField, KeyFieldName))
                .Select(field => new { field.Property, field.Name, Selection = field.ValueSelection, PreferredProviders = field.PreferredProviders.ToArray() }).ToArray());
            shapes.Add(new("project", null, fields, []));
            var links = node.Links.Select(link => new EntityReadLink(link.Link.Term.Identifier.AbsoluteUri, Add(link.Target))).ToArray();
            shapes[index] = new("project", null, fields, links);
            return index;
        }
        Add(_root);
        return new(Guid.NewGuid(), EntityReadRecipe.Value(_root.Node.IdentityValue(identity)), shapes.ToArray());
    }

}
