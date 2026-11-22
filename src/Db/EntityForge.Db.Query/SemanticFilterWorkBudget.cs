using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Query;

// One budget per execution, shared by branches, recursive filters and selection batches.
// Repeated loads count again: this limits work, rather than distinct result entities.
internal sealed class SemanticFilterWorkBudget
{
    private readonly int _maximumTargetVisits;
    private int _remaining;

    public SemanticFilterWorkBudget(int maximumTargetVisits = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTargetVisits);
        _maximumTargetVisits = maximumTargetVisits;
        _remaining = maximumTargetVisits;
    }

    public SemanticEntityIdentity[] AdmitTargets(IEnumerable<SemanticEntityIdentity> candidates,
        CancellationToken cancellationToken)
    {
        var targets = new HashSet<SemanticEntityIdentity>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (targets.Contains(candidate))
            {
                continue;
            }

            if (_remaining == 0)
            {
                throw new EntityForgeQueryCapabilityException(
                    $"The relationship filter exceeds the supported budget of {_maximumTargetVisits} target visits. " +
                    "Narrow the root set or relationship filter. This limit is independent of projection expansion.");
            }

            _remaining--;
            targets.Add(candidate);
        }
        return targets.ToArray();
    }
}
