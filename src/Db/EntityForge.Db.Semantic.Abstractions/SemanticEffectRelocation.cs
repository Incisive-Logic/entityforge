using System.Runtime.CompilerServices;

namespace EntityForge.Db.Semantic;

// Carries relocation of a compiled logical plan without coupling the engine to a storage implementation.
internal static class SemanticEffectRelocation
{
    internal sealed record Origin(SemanticCommittedEffects Effects, DatabaseVersion From, DatabaseVersion To);

    private static readonly ConditionalWeakTable<SemanticCommittedEffects, Origin> Origins = new();

    internal static void Register(SemanticCommittedEffects source, SemanticCommittedEffects target,
        DatabaseVersion from, DatabaseVersion to) => Origins.Add(target, new(source, from, to));

    internal static Origin? Find(SemanticCommittedEffects effects) => Origins.TryGetValue(effects, out var origin) ? origin : null;
}
