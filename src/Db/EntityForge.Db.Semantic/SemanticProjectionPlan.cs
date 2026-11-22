using EntityForge.Db.AccessPaths;
using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Semantic;

internal sealed record SemanticProjectionPlan(
    IReadOnlyList<SemanticProjectionMutationBatch> Batches,
    IReadOnlyList<OrderedPageTreeMutation<SemanticAccessKey, byte[]>> OrderedMutations,
    IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> SharedMutations,
    IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> ConceptMutations,
    IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> OutgoingMutations,
    IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> IncomingMutations);

internal sealed class SemanticProjectionPlanBuilder(
    Func<SemanticAccessKey, int> keyEncodedLength,
    SemanticProjectionTelemetry telemetry)
{
    public SemanticProjectionPlanAccumulator Begin(int totalEffects) =>
        new(totalEffects, keyEncodedLength, telemetry);
}

internal sealed class SemanticProjectionPlanAccumulator(
    int totalEffects,
    Func<SemanticAccessKey, int> keyEncodedLength,
    SemanticProjectionTelemetry telemetry)
{
    private readonly Dictionary<SemanticProjectionRole,
        List<OrderedPageTreeMutation<SemanticAccessKey, byte[]>>> _mutations = [];

    private readonly Dictionary<SemanticProjectionRole, int> _effectCounts = [];

    public void AddMutation(int effectCount, OrderedPageTreeMutation<SemanticAccessKey, byte[]> mutation)
    {
        SemanticProjectionRole role = Role(mutation.Key.Family);
        if (!_mutations.TryGetValue(role, out List<OrderedPageTreeMutation<SemanticAccessKey, byte[]>>? items))
        {
            _mutations.Add(role, items = []);
        }

        items.Add(mutation);
        _effectCounts[role] = checked(_effectCounts.GetValueOrDefault(role) + effectCount);
    }

    public void Add(int effectCount,
        IEnumerable<OrderedPageTreeMutation<SemanticAccessKey, byte[]>> mutations)
    {
        var affectedRoles = new HashSet<SemanticProjectionRole>();
        foreach (OrderedPageTreeMutation<SemanticAccessKey, byte[]> mutation in mutations)
        {
            SemanticProjectionRole role = Role(mutation.Key.Family);
            if (!_mutations.TryGetValue(role, out List<OrderedPageTreeMutation<SemanticAccessKey, byte[]>>? items))
            {
                _mutations.Add(role, items = []);
            }

            items.Add(mutation);
            affectedRoles.Add(role);
        }
        foreach (SemanticProjectionRole role in affectedRoles)
        {
            _effectCounts[role] = checked(_effectCounts.GetValueOrDefault(role) + effectCount);
        }
    }

    public SemanticProjectionPlan Complete(
        IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> conceptMutations,
        IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> outgoingMutations,
        IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> incomingMutations,
        TimeSpan duration)
    {
        SemanticProjectionMutationBatch[] batches = _mutations
            .OrderBy(pair => pair.Key)
            .Select(pair => new SemanticProjectionMutationBatch(pair.Key,
                _effectCounts.GetValueOrDefault(pair.Key), pair.Value))
            .ToArray();
        OrderedPageTreeMutation<SemanticAccessKey, byte[]>[] ordered = batches
            .Where(batch => batch.Role is SemanticProjectionRole.StringRange)
            .SelectMany(batch => batch.Mutations).ToArray();
        AppendMergeMutation<SemanticAccessKey, byte[]>[] shared = batches
            .Where(batch => !IsFieldRole(batch.Role) && batch.Role is not SemanticProjectionRole.StringRange)
            .SelectMany(batch => batch.Mutations)
            .Select(ToAppendMergeMutation).ToArray();
        foreach (SemanticProjectionMutationBatch batch in batches)
        {
            telemetry.Plan(batch.Role, batch.EffectCount, batch.Mutations.Count,
                EncodedBytes(batch.Mutations));
        }

        if (conceptMutations.Count != 0)
        {
            telemetry.Plan(SemanticProjectionRole.ConceptMembership, conceptMutations.Count,
                conceptMutations.Count, conceptMutations.Sum(item =>
                    (long)keyEncodedLength(item.Key) + item.Value.Length));
        }

        PlanAppend(SemanticProjectionRole.OutgoingRelationship, outgoingMutations);
        PlanAppend(SemanticProjectionRole.IncomingRelationship, incomingMutations);
        telemetry.Plan(SemanticProjectionRole.SharedSemanticExact, totalEffects,
            shared.Length, EncodedBytes(shared), duration);
        return new(batches, ordered, shared, conceptMutations, outgoingMutations, incomingMutations);
    }

    private void PlanAppend(SemanticProjectionRole role,
        IReadOnlyList<AppendMergeMutation<SemanticAccessKey, byte[]>> mutations)
    {
        if (mutations.Count != 0)
        {
            telemetry.Plan(role, mutations.Count, mutations.Count, mutations.Sum(item =>
                (long)keyEncodedLength(item.Key) + item.Value.Length));
        }
    }

    private long EncodedBytes(IEnumerable<OrderedPageTreeMutation<SemanticAccessKey, byte[]>> mutations) =>
        mutations.Sum(item => (long)keyEncodedLength(item.Key) + item.Value.Length);

    private long EncodedBytes(IEnumerable<AppendMergeMutation<SemanticAccessKey, byte[]>> mutations) =>
        mutations.Sum(item => (long)keyEncodedLength(item.Key) + item.Value.Length);

    private static AppendMergeMutation<SemanticAccessKey, byte[]> ToAppendMergeMutation(
        OrderedPageTreeMutation<SemanticAccessKey, byte[]> mutation) => new(
            mutation.Kind is OrderedPageTreeMutationKind.Add
                ? AppendMergeMutationKind.Add : AppendMergeMutationKind.Remove,
            mutation.Key, mutation.Value);

    private static bool IsFieldRole(SemanticProjectionRole role) =>
        role is SemanticProjectionRole.FieldEvidence or SemanticProjectionRole.FieldMembership or
            SemanticProjectionRole.SubjectField;

    private static SemanticProjectionRole Role(SemanticAccessFamily family) => family switch
    {
        SemanticAccessFamily.Identity => SemanticProjectionRole.Identity,
        SemanticAccessFamily.Concept => SemanticProjectionRole.ConceptMembership,
        SemanticAccessFamily.FieldEquality => SemanticProjectionRole.FieldMembership,
        SemanticAccessFamily.StringRange or SemanticAccessFamily.EntityStringOrder =>
            SemanticProjectionRole.StringRange,
        SemanticAccessFamily.SubjectField => SemanticProjectionRole.SubjectField,
        SemanticAccessFamily.FieldAssertion => SemanticProjectionRole.FieldEvidence,
        SemanticAccessFamily.OutgoingRelationship => SemanticProjectionRole.OutgoingRelationship,
        SemanticAccessFamily.IncomingRelationship => SemanticProjectionRole.IncomingRelationship,
        SemanticAccessFamily.RelationshipAssertion => SemanticProjectionRole.RelationshipEvidence,
        SemanticAccessFamily.AtomDirectory => SemanticProjectionRole.AtomDirectory,
        SemanticAccessFamily.AtomPayload => SemanticProjectionRole.AtomPayload,
        SemanticAccessFamily.AtomSequence => SemanticProjectionRole.AtomSequence,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family,
            "Unsupported Semantic access family.")
    };
}
