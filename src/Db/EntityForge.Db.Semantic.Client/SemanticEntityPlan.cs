using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Mapping;
using EntityForge.Query.Projection;
using EntityForge.Query.Values;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

// Immutable resolved mapping closure and pure value-to-session construction. No execution client is retained.
internal sealed class SemanticEntityPlan
{
    private const int MaximumMappingTypes = 64;
    private const int FirstPage = 0;
    private const int GuidByteLength = 16;
    internal const string KeyFieldName = "$aggregateKey";
    internal static readonly SemanticTerm KeyField = new(new Uri("urn:entityforge:aggregate:original-key:v1"), SemanticTermRole.Property);
    internal static readonly SemanticIdentityScheme OwnedScheme = new(new Uri("urn:entityforge:aggregate:owned-slot:v1"), "Owned slot", SemanticValueKind.String);
    private readonly SemanticTargetBinding? _rootBindingOverride;
    private readonly SemanticApplicationIdentity _application;
    private readonly SemanticEntityStoreLimits _limits;
    private readonly PlanNode _root;

    internal PlanNode Root => _root;

    internal IReadOnlyList<SemanticTargetBinding> TargetBindings
    {
        get;
    }

    private readonly EntityMappingManifest[] _manifests;

    internal IReadOnlyList<EntityMappingManifest> Manifests
    {
        get;
    }

    private readonly IReadOnlyDictionary<AggregateNode, PlanNode> _plans;

    private static readonly SemanticEntityPlanCache LoweringCache = new();

    internal static SemanticEntityPlan FromDefinition(SemanticEntityDefinition definition,
        SemanticApplicationIdentity application, SemanticEntityStoreLimits limits, SemanticTargetBinding? rootBinding) =>
        LoweringCache.Get(definition, application, limits, rootBinding);

    internal SemanticEntityPlan(AggregateNode map,
        SemanticApplicationIdentity application, SemanticEntityStoreLimits limits, SemanticTargetBinding? rootBinding = null)
    {
        _application = application;
        _limits = limits;
        _rootBindingOverride = rootBinding;
        var nodes = new Dictionary<AggregateNode, PlanNode>(ReferenceEqualityComparer.Instance);
        var identities = new Dictionary<(string, string), AggregateNode>();
        PlanNode Resolve(AggregateNode node)
        {
            if (nodes.TryGetValue(node, out PlanNode? prior))
            {
                return prior;
            }

            if (nodes.Count == MaximumMappingTypes)
            {
                throw new NotSupportedException("An aggregate mapping has too many types.");
            }

            if (node.Scheme.Identifier == OwnedScheme.Identifier)
            {
                throw new ArgumentException("The aggregate owned-slot identity scheme is reserved.");
            }

            var key = (node.ScalarManifest.Model.Value, node.ScalarManifest.EntityType.Value);
            if (!identities.TryAdd(key, node))
            {
                throw new ArgumentException("An aggregate closure contains multiple maps for the same persistent type.");
            }

            if (node.ScalarProjection.Fields.Any(field => field.Name == KeyFieldName || field.Property == KeyField))
            {
                throw new ArgumentException("The aggregate key field is reserved.");
            }

            var plan = new PlanNode(node);
            nodes.Add(node, plan);
            var links = node.Links.OrderBy(link => link.Identity, StringComparer.Ordinal)
                .Select(link => new PlanLink(link, Resolve(link.Target()))).ToArray();
            EntityMemberContract[] members = [.. node.ScalarManifest.Members,
                .. links.Select(link => new EntityMemberContract(
                link.Link.Identity, link.Link.Member.Name, link.Link.Member.Kind, link.Link.Member.CollectionKind,
                link.Link.Nullable, link.Link.CollectionCodec ?? (link.Link.Cascades ? "entityforge/owned-slot-v1" :
                    link.Target.Node.ScalarManifest.Members.Single(member => member.Kind == EntityMemberKind.Identity).ValueCodec), 1))];
            EntityProfileMemberContract[] bindings = [.. node.ScalarManifest.ProfileBindings,
                .. links.Select(link => new EntityProfileMemberContract(
                link.Link.Identity, link.Link.Term.Identifier.AbsoluteUri, "assertion-relationship", link.Target.Node.ScalarManifest.ProfileTypeBinding,
                link.Link.Cascades ? OwnedScheme.Identifier.AbsoluteUri :
                    link.Target.Node.ScalarManifest.ProfileBindings.Single(binding => binding.MemberIdentity ==
                        link.Target.Node.ScalarManifest.Members.Single(member => member.Kind == EntityMemberKind.Identity).Identity).StoredBinding,
                "outgoing"))];
            plan.Initialize(links, new(node.ScalarManifest.Model, AggregateNode.Convention, node.ScalarManifest.EntityType,
                DatabaseProfileId.Semantic, node.ScalarManifest.ProfileTypeBinding, members, bindings));
            return plan;
        }
        if (rootBinding is not null)
        {
            var scalar = map.ScalarTargetBinding(rootBinding.TargetVocabulary);
            var expected = new SemanticTargetBinding(rootBinding.Identifier, rootBinding.Version,
                rootBinding.TargetVocabulary, scalar.Concept, scalar.IdentityScheme, scalar.Members);
            if (map.Links.Count != 0 || expected.Fingerprint != rootBinding.Fingerprint)
            {
                throw new ArgumentException("The target binding does not describe the captured scalar definition.", nameof(rootBinding));
            }
        }
        _root = Resolve(map);
        _plans = nodes;
        _manifests = nodes.Values.Select(node => node.Manifest!).OrderBy(item => item.Model.Value, StringComparer.Ordinal)
            .ThenBy(item => item.EntityType.Value, StringComparer.Ordinal).ToArray();
        Manifests = Array.AsReadOnly(_manifests);
        foreach (PlanNode node in nodes.Values)
        {
            SemanticTargetMemberBinding[] members = [.. node.Node.ScalarTargetBinding(_application.TargetVocabulary).Members,
                new(KeyFieldName, KeyField, SemanticValueKind.String),
                .. node.Links.Select(link => new SemanticTargetMemberBinding(
                    link.Link.Identity, link.Link.Term, targetConcept: link.Target.Node.ScalarProjection.Concept,
                    targetIdentityScheme: link.Link.Cascades ? OwnedScheme : link.Target.Node.Scheme))];
            SemanticTargetBinding Binding(string role, SemanticIdentityScheme scheme) => new(
                new Uri("urn:entityforge:aggregate:binding:" + node.Manifest!.Fingerprint + ":" + role),
                AggregateNode.Convention, _application.TargetVocabulary, node.Node.ScalarProjection.Concept, scheme, members);
            node.InitializeBindings(node == _root && _rootBindingOverride is not null ? _rootBindingOverride : Binding("root", node.Node.Scheme),
                Binding("owned", OwnedScheme));
        }
        TargetBindings = Array.AsReadOnly(nodes.Values.SelectMany(node => new[] { node.RootBinding!, node.OwnedBinding! }).ToArray());
    }

    internal SemanticEntityWrite PrepareSave(Guid destination, object entity, SemanticEntityWriteOperation operation,
        SemanticSourcePosition? predecessor, bool guarded)
    {
        var captured = CaptureSaveCore(destination, entity, operation, predecessor, guarded);
        return PrepareCaptured(destination, EntityValueCapture.Decode(captured._payload)!, operation, predecessor, guarded, false);
    }

    private SemanticEntityWrite PrepareSaveCore(Guid destination, object entity, SemanticEntityWriteOperation operation,
        SemanticSourcePosition? predecessor, bool guarded, bool retainCompiled = false)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(operation);
        SemanticEntityIdentity rootIdentity = _root.Node.ReadIdentity(entity);
        var changes = new List<SemanticEntityChange>();
        var (staged, byInstance) = CollectInput(entity, rootIdentity);
        foreach (var item in staged)
        {
            SemanticEntityIdentity original = item.Plan.Node.ReadIdentity(item.Entity);
            SemanticFieldChange[] fields = [.. item.Plan.Node.ReadFields(item.Entity).OrderBy(field => field.Field.Identifier.AbsoluteUri, StringComparer.Ordinal),
                new(KeyField, SemanticFieldChangeKind.Set,
                new StringSemanticValue(SemanticValueConverter.ToLexical(original.Value)))];
            var links = new List<SemanticRelationshipChange>();
            foreach (var captured in item.Links)
            {
                PlanLink link = captured.Link;
                object? target = captured.Value;
                if (target is null)
                {
                    continue;
                }

                SemanticEntityIdentity targetIdentity = LinkIdentity(item.Identity, link, target, byInstance);
                links.Add(new(link.Link.Term, SemanticRelationshipChangeKind.Add, targetIdentity));
            }
            changes.Add(new(ChangeId(operation.OperationId, item.Identity), SemanticEntityChangeKind.ReplaceSourceState,
                item.Identity, Provenance(item.Identity, item.Plan, operation), fields, links));
        }
        return Prepare(destination, rootIdentity, operation, predecessor, changes, guarded, retainCompiled);
    }

    private (List<(PlanNode Plan, object Entity, SemanticEntityIdentity Identity, (PlanLink Link, object? Value)[] Links)> Staged,
        Dictionary<object, SemanticEntityIdentity> ByInstance) CollectInput(object entity, SemanticEntityIdentity rootIdentity, CancellationToken token = default)
    {
        var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var byInstance = new Dictionary<object, SemanticEntityIdentity>(ReferenceEqualityComparer.Instance);
        var staged = new List<(PlanNode Plan, object Entity, SemanticEntityIdentity Identity, (PlanLink Link, object? Value)[] Links)>();
        void Collect(PlanNode plan, object value, SemanticEntityIdentity identity, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > _limits.Depth || staged.Count == _limits.Entities)
            {
                throw new InvalidOperationException("The aggregate write exceeds its depth or entity limit.");
            }

            if (!owned.Add(value))
            {
                throw new InvalidOperationException("Owned objects cannot be cyclic or shared between ownership slots.");
            }

            byInstance.Add(value, identity);
            var links = plan.Links.Select(link => (Link: link, Value: ReadLink(link, value, plan.Node.DepthCost != 0))).ToArray();
            staged.Add((plan, value, identity, links));
            foreach (var link in links.Where(link => link.Link.Link.Cascades))
            {
                if (link.Value is not null)
                {
                    Collect(link.Link.Target, link.Value, OwnedIdentity(identity, link.Link), depth + link.Link.Target.Node.DepthCost);
                }
            }
        }
        Collect(_root, entity, rootIdentity, 0);
        return (staged, byInstance);
    }

    private SemanticEntityIdentity LinkIdentity(SemanticEntityIdentity owner, PlanLink link, object target,
        Dictionary<object, SemanticEntityIdentity> byInstance)
    {
        var identity = link.Link.Cascades ? OwnedIdentity(owner, link)
            : byInstance.GetValueOrDefault(target) ?? link.Target.Node.ReadIdentity(target);
        if (!link.Link.Cascades && identity.Scheme == OwnedScheme)
        {
            throw new NotSupportedException("References target independent entities, not objects in owned slots.");
        }

        return identity;
    }

    internal sealed record RoutingCapture(Uri SourceSystem, Uri SourcePartition,
        IReadOnlyList<SemanticEntityIdentity> Subjects, IReadOnlyList<SemanticEntityIdentity> Targets);

    // Discovery shares identity/link traversal with compilation, but produces no source
    // session, provenance, change pages, hashes or wire frames. Owners still compile
    // and enforce the complete session/byte limits before a proposal can be selected.
    internal RoutingCapture CaptureRouting(object value, bool delete, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = delete ? _root.Node.IdentityValue(value) : _root.Node.ReadIdentity(value);
        if (delete)
        {
            return new(_application.Identifier, Partition(root), [root], []);
        }

        var (staged, byInstance) = CollectInput(value, root, token);
        var subjects = new List<SemanticEntityIdentity>();
        var targets = new List<SemanticEntityIdentity>();
        foreach (var item in staged)
        {
            token.ThrowIfCancellationRequested();
            _ = item.Plan.Node.ReadIdentity(item.Entity);
            // Validate captured scalar types as well as links before routing. The
            // compiler remains responsible for constructing and bounding the session.
            _ = item.Plan.Node.ReadFields(item.Entity);
            subjects.Add(item.Identity);
            foreach (var link in item.Links)
            {
                if (link.Value is { } target)
                {
                    targets.Add(LinkIdentity(item.Identity, link.Link, target, byInstance));
                }
            }
        }
        return new(_application.Identifier, Partition(root), subjects.ToArray(), targets.ToArray());
    }

    internal SemanticEntityWrite PrepareDeleteCore(Guid destination, object identity, SemanticEntityWriteOperation operation,
        SemanticSourcePosition? predecessor, bool guarded, bool retainCompiled = false)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(operation);
        return PrepareDeleteIdentity(destination, _root.Node.IdentityValue(identity), operation, predecessor, guarded, retainCompiled);
    }

    private SemanticEntityWrite PrepareDeleteIdentity(Guid destination, SemanticEntityIdentity root, SemanticEntityWriteOperation operation,
        SemanticSourcePosition? predecessor, bool guarded, bool retainCompiled = false)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Prepare(destination, root, operation, predecessor, [new(ChangeId(operation.OperationId, root),
            SemanticEntityChangeKind.RetractSourceEntity, root, Provenance(root, _root, operation))], guarded, retainCompiled);
    }

    private SemanticEntityWrite Prepare(Guid destination, SemanticEntityIdentity root, SemanticEntityWriteOperation operation,
        SemanticSourcePosition? predecessor, IReadOnlyList<SemanticEntityChange> changes, bool guarded, bool retainCompiled)
    {
        var source = new SemanticChangeSessionSource(_application.Identifier, Partition(root), _application.Version,
            _application.GenerationId, operation.OperationId.ToString("N"));
        var start = new SemanticChangeSessionStart(operation.OperationId, SemanticChangeSessionKind.SourceSnapshot, source,
            _application.ProducerId, operation.ObservedAt, progressPrecondition: !guarded ? null : predecessor is null ?
                SemanticSourceProgressPrecondition.Initial : SemanticSourceProgressPrecondition.After(predecessor));
        var page = new SemanticChangeSessionPage(operation.OperationId, FirstPage, changes);
        var codec = new SemanticChangeSessionTransportCodec();
        var hashes = codec.ComputeHashes(start, [page]);
        var close = new SemanticChangeSessionClose(operation.OperationId, 1, changes.Count, hashes.ContentHash,
            operation.ObservedAt, hashes.OperationHash);
        byte[] startBytes = codec.Serialize(start), pageBytes = codec.Serialize(page), closeBytes = codec.Serialize(close);
        if ((long)startBytes.Length + pageBytes.Length + closeBytes.Length > _limits.Bytes)
        {
            throw new InvalidOperationException("The aggregate write exceeds its encoded byte limit.");
        }
        // Round-trip the transport bytes to freeze caller-owned binary values before any registration or write.
        return new(destination, _manifests, new(codec.DeserializeStart(startBytes),
            [codec.DeserializePage(pageBytes)], codec.DeserializeClose(closeBytes)), _limits,
            [startBytes, pageBytes, closeBytes], retainCompiled);
    }

    private object? ReadLink(PlanLink link, object owner, bool domain, bool normalizeCollections = true)
    {
        object? value;
        if (link.Link.Capture is not null)
        {
            value = link.Link.Capture(owner, _limits.Entities, normalizeCollections);
        }
        else if (owner is Entity input)
        {
            input.Members.TryGetValue(link.Link.Identity, out value);
            if (value is not null && domain && link.Target.Node.DepthCost != 0)
            {
                if (link.Link.Cascades && value is not Entity)
                {
                    throw new ArgumentException("An owned input member requires an Entity.");
                }

                if (!link.Link.Cascades && value is not EntityReference && domain)
                {
                    throw new ArgumentException("An independent input reference requires an EntityReference.");
                }
            }
        }
        else
        {
            value = link.Link.Member.Read(owner);
        }

        if (value is null && !link.Link.Nullable)
        {
            throw new InvalidOperationException($"Required aggregate link '{link.Link.Member.Name}' is null.");
        }

        return value;
    }

    private SemanticProvenance Provenance(SemanticEntityIdentity entity, PlanNode node, SemanticEntityWriteOperation operation)
    {
        SemanticTargetBinding binding = (entity.Scheme == OwnedScheme ? node.OwnedBinding : node.RootBinding)!;
        return new(_application.Identifier, Key(entity), operation.ObservedAt, binding.TargetVocabulary,
            targetBinding: new(binding.Identifier, binding.Version, binding.Fingerprint));
    }

    internal Uri Partition(SemanticEntityIdentity root) => SemanticEntityPlacement.Partition(root);

    internal static string Key(SemanticEntityIdentity identity) => EntityIdentityEncoding.Text("semantic-entity-key/v2",
        identity.Concept.Identifier.AbsoluteUri, identity.Scheme.Identifier.AbsoluteUri,
        identity.Value.Kind.ToString(), SemanticValueConverter.ToLexical(identity.Value));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Guid ChangeId(Guid operation, SemanticEntityIdentity entity) => new(SHA256.HashData(Encoding.UTF8.GetBytes(operation.ToString("N") + Key(entity))).AsSpan(0, GuidByteLength));

    internal static SemanticEntityIdentity OwnedIdentity(SemanticEntityIdentity parent, PlanLink link) =>
        new(link.Target.Node.ScalarProjection.Concept, OwnedScheme,
            new StringSemanticValue(EntityIdentityEncoding.Hash("semantic-owned-slot/v2", Key(parent), link.Link.Identity)));

    internal sealed class PlanNode(AggregateNode node)
    {
        private (IReadOnlyList<PlanLink> Links, EntityMappingManifest Manifest)? _shape;
        private (SemanticTargetBinding Root, SemanticTargetBinding Owned)? _bindings;

        public AggregateNode Node { get; } = node;

        public IReadOnlyList<PlanLink> Links => _shape!.Value.Links;

        public EntityMappingManifest Manifest => _shape!.Value.Manifest;

        public SemanticTargetBinding RootBinding => _bindings!.Value.Root;

        public SemanticTargetBinding OwnedBinding => _bindings!.Value.Owned;

        // Recursive maps allocate nodes first; each immutable shape/binding is installed exactly once.
        internal void Initialize(PlanLink[] links, EntityMappingManifest manifest)
        {
            if (_shape is not null)
            {
                throw new InvalidOperationException("The mapping shape is already initialized.");
            }

            _shape = (Array.AsReadOnly(links), manifest);
        }

        internal void InitializeBindings(SemanticTargetBinding root, SemanticTargetBinding owned)
        {
            if (_bindings is not null)
            {
                throw new InvalidOperationException("The mapping bindings are already initialized.");
            }

            _bindings = (root, owned);
        }
    }

    internal sealed record PlanLink(AggregateLink Link, PlanNode Target);

    internal SemanticEntityRequest CaptureSaveCore(Guid destination, object entity, SemanticEntityWriteOperation operation,
        SemanticSourcePosition? predecessor, bool guarded)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(operation);
        var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var keys = new Dictionary<PlanNode, Dictionary<object, object>>(ReferenceEqualityComparer.Instance);
        Dictionary<object, object> Keys(PlanNode plan)
        {
            if (!keys.TryGetValue(plan, out var values))
            {
                keys.Add(plan, values = new(ReferenceEqualityComparer.Instance));
            }

            return values;
        }
        Entity Capture(PlanNode plan, object value, int depth)
        {
            if (depth > _limits.Depth || owned.Count >= _limits.Entities)
            {
                throw new InvalidOperationException("Captured entities exceed their bounds.");
            }

            if (!owned.Add(value))
            {
                throw new InvalidOperationException("Owned values cannot be cyclic or shared.");
            }
            // Capture CLR scalar getters once, then validate the detached values through the input path.
            if (value is Entity supplied)
            {
                _ = plan.Node.ReadIdentity(supplied);
            }

            var scalars = value is Entity input
                ? new Entity(plan.Node.ScalarManifest.Members.ToDictionary(member => member.Identity,
                    member => input.Members.GetValueOrDefault(member.Identity), StringComparer.Ordinal))
                : plan.Node.CaptureScalars(value);
            _ = plan.Node.ReadIdentity(scalars);
            _ = plan.Node.ReadFields(scalars);
            Keys(plan).Add(value, scalars.Members[plan.Node.ScalarManifest.Members.Single(member => member.Kind == EntityMemberKind.Identity).Identity]!);
            var values = scalars.Members.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var link in plan.Links)
            {
                object? captured = ReadLink(link, value, true, normalizeCollections: false);
                if (captured is null)
                {
                    values.Add(link.Link.Identity, null);
                    continue;
                }
                PlanNode? target = DomainTarget(link);
                object Element(object element) => link.Link.Member.Kind switch
                {
                    EntityMemberKind.Owned => Capture(target!, element, depth + 1),
                    EntityMemberKind.Reference => new PendingReference(target!, element),
                    _ => element
                };
                if (link.Link.Member.IsCollection)
                {
                    var elements = new List<object?>();
                    for (Entity? cell = (Entity?)((Entity)captured).Members["First"]; cell is not null;
                        cell = (Entity?)cell.Members["Next"])
                    {
                        elements.Add(Element(cell.Members["Value"]!));
                    }

                    values.Add(link.Link.Identity, new EntityCollection(link.Link.Member.CollectionKind, elements));
                }
                else
                {
                    values.Add(link.Link.Identity, Element(captured));
                }
            }
            return new Entity(values);
        }
        EntityReference Reference(PlanNode target, object value)
        {
            if (value is EntityReference reference)
            {
                _ = target.Node.InputIdentity(reference.Identity);
                return reference;
            }
            if (!ReferenceEquals(value, entity) && owned.Contains(value))
            {
                throw new NotSupportedException("References target independent entities, not objects in owned slots.");
            }

            var identity = target.Node.ScalarManifest.Members.Single(member => member.Kind == EntityMemberKind.Identity);
            if (!Keys(target).TryGetValue(value, out object? key))
            {
                key = value is Entity input ? input.Members[identity.Identity]! : target.Node.CaptureKey(value);
                Keys(target).Add(value, key);
            }
            _ = target.Node.InputIdentity(key);
            return new EntityReference(key);
        }
        object? Resolve(object? value) => value switch
        {
            PendingReference reference => Reference(reference.Target, reference.Value),
            Entity captured => new Entity(captured.Members.ToDictionary(pair => pair.Key, pair => Resolve(pair.Value), StringComparer.Ordinal)),
            EntityCollection collection => new EntityCollection(collection.Kind, collection.Items.Select(Resolve).ToArray()),
            _ => value
        };
        Entity Normalize(PlanNode plan, Entity frozen)
        {
            var values = frozen.Members.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var link in plan.Links)
            {
                object? normalized = ReadLink(link, frozen, true);
                if (normalized is null)
                {
                    continue;
                }

                object Element(object item) => link.Link.Member.Kind == EntityMemberKind.Owned
                    ? Normalize(DomainTarget(link)!, (Entity)item) : item;
                if (link.Link.Member.IsCollection)
                {
                    var elements = new List<object?>();
                    for (Entity? cell = (Entity?)((Entity)normalized).Members["First"]; cell is not null;
                        cell = (Entity?)cell.Members["Next"])
                    {
                        elements.Add(Element(cell.Members["Value"]!));
                    }

                    values[link.Link.Identity] = new EntityCollection(link.Link.Member.CollectionKind, elements);
                }
                else
                {
                    values[link.Link.Identity] = Element(normalized);
                }
            }
            return new Entity(values);
        }
        var capturedRoot = Capture(_root, entity, 0);
        var frozenRoot = (Entity)Resolve(capturedRoot)!;
        return new(destination, Definition(), _application, _limits,
            _rootBindingOverride, Normalize(_root, frozenRoot), operation, predecessor, guarded, false);
    }

    internal SemanticEntityRequest CaptureDeleteCore(Guid destination, object identity, SemanticEntityWriteOperation operation,
        SemanticSourcePosition? predecessor, bool guarded)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(operation);
        _ = _root.Node.InputIdentity(identity);
        return new(destination, Definition(), _application, _limits,
            _rootBindingOverride, identity, operation, predecessor, guarded, true);
    }

    private SemanticEntityDefinition Definition() => SemanticEntityDefinition.Capture(_root.Node,
        (node, link) => DomainTarget(_plans[node].Links.Single(item => item.Link.Identity == link.Identity))?.Node);

    internal SemanticEntityWrite PrepareCaptured(Guid destination, object value, SemanticEntityWriteOperation operation,
        SemanticSourcePosition? predecessor, bool guarded, bool delete, bool retainCompiled = false) => delete
            ? PrepareDeleteCore(destination, value, operation, predecessor, guarded, retainCompiled)
            : PrepareSaveCore(destination, value, operation, predecessor, guarded, retainCompiled);

    private sealed record PendingReference(PlanNode Target, object Value);

    private static PlanNode? DomainTarget(PlanLink link)
    {
        if (!link.Link.Member.IsCollection)
        {
            return link.Target;
        }

        if (link.Link.Member.Kind == EntityMemberKind.Scalar)
        {
            return null;
        }

        var cell = link.Target.Links.Single(item => item.Link.Identity == "First").Target;
        return cell.Links.Single(item => item.Link.Identity == "Value").Target;
    }
}
