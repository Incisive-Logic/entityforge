using EntityForge.Db.Mapping;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

internal sealed class SemanticEntityPlanCache
{
    internal const int MaximumEntries = ImmutableEntityPlanCache<SemanticEntityPlan>.MaximumEntries;
    internal const int MaximumKeyBytes = ImmutableEntityPlanCache<SemanticEntityPlan>.MaximumKeyBytes;
    internal const int MaximumRetainedKeyBytes = ImmutableEntityPlanCache<SemanticEntityPlan>.MaximumRetainedKeyBytes;
    private readonly ImmutableEntityPlanCache<SemanticEntityPlan> _plans = new();

    internal int Count => _plans.Count;

    internal int RetainedKeyBytes => _plans.RetainedKeyBytes;

    internal SemanticEntityPlan Get(SemanticEntityDefinition definition, SemanticApplicationIdentity application,
        SemanticEntityStoreLimits limits, SemanticTargetBinding? binding)
    {
        SemanticEntityPlan Create() => new(SemanticEntityStructure.FromDefinition(definition).Node, application, limits, binding);
        byte[] key;
        try
        {
            key = DeliveryBinary.Encode(w => SemanticDeliveryMetadata.WritePlan(w, definition, application, limits,
            binding is null ? null : SemanticEntityRequest.BindingMetadata.From(binding)));
        }
        catch (InvalidDataException)
        {
            return Create();
        } // Cache admission must not narrow local planning limits.
        return _plans.Get(key, Create);
    }
}
