using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Semantic;

internal static class SemanticPhysicalMutationGroup
{
    internal static Task<AppendMergeMutationPlan<TKey, byte[]>>? Plan<TKey>(
        AppendMergeAccessPath<TKey, byte[]> access, IReadOnlyList<AppendMergeMutation<TKey, byte[]>> mutations,
        long version, CancellationToken token,
        IReadOnlyList<(long Version, IReadOnlyList<AppendMergeMutation<TKey, byte[]>> Mutations)>? group)
        where TKey : notnull => mutations.Count == 0 ? null : Task.Run(() =>
        {
            if (group is null || group.Count == 1)
            {
                return access.PlanMutations(mutations, version, token);
            }

            var plans = group.Where(item => item.Mutations.Count != 0)
                .Select(item => access.PlanMutations(item.Mutations, item.Version, token)).ToArray();
            token.ThrowIfCancellationRequested();
            return access.PlanMutationGroup(plans);
        }, token);
}
