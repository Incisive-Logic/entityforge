namespace EntityForge.Db.Query.Planning;

// Each transition describes a complete branch match on one captured candidate
// path. Matching/profile identity remain outside this combinatorial operation.
internal readonly record struct PathAlternativeMatch(int Start, int End, int Multiplicity);

internal sealed class PathMultiplicityBudgetException(string message) : Exception(message);

internal static partial class PathAlternativeMultiplicity
{
    internal static int Count(int pathLength, int minimumIterations, int? maximumIterations,
        IReadOnlyList<PathAlternativeMatch> matches, int maximumPositions, int maximumTransitions,
        int maximumMultiplicity, long maximumWork, CancellationToken cancellationToken = default,
        Action<long>? chargeWork = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentOutOfRangeException.ThrowIfNegative(pathLength);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumIterations);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumPositions);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTransitions);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumMultiplicity);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumWork);
        if (maximumIterations is { } upper && upper < minimumIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumIterations));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (pathLength >= maximumPositions || matches.Count > maximumTransitions)
        {
            throw new PathMultiplicityBudgetException("Path decomposition exceeds its position or transition budget.");
        }
        long work = 0;
        void Charge(long amount = 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (amount > maximumWork - work)
            {
                throw new PathMultiplicityBudgetException("Path decomposition exceeds its work budget.");
            }
            work += amount;
            chargeWork?.Invoke(amount);
        }
        foreach (var match in matches)
        {
            Charge();
            if (match.Start < 0 || match.End <= match.Start || match.End > pathLength || match.Multiplicity < 0)
            {
                throw new ArgumentException("A branch match requires positive path length and nonnegative multiplicity.", nameof(matches));
            }
        }
        int maximum = Math.Min(maximumIterations ?? pathLength, pathLength);
        if (minimumIterations > maximum)
        {
            return 0;
        }
        // Saturating partial counts avoids overflow without rejecting a large
        // dead-end prefix. Only multiplicity reaching the accepted endpoint and
        // iteration range is compared with the result budget.
        long ceiling = (long)maximumMultiplicity + 1;
        var previous = new long[pathLength + 1];
        var next = new long[pathLength + 1];
        previous[0] = 1;
        long result = pathLength == 0 && minimumIterations == 0 ? 1 : 0;
        for (int iteration = 1; iteration <= maximum; iteration++)
        {
            Charge(next.Length);
            Array.Clear(next);
            bool any = false;
            foreach (var match in matches)
            {
                Charge();
                if (previous[match.Start] == 0 || match.Multiplicity == 0)
                {
                    continue;
                }
                next[match.End] = Math.Min(ceiling, next[match.End] + previous[match.Start] * match.Multiplicity);
                any = true;
            }
            if (iteration >= minimumIterations)
            {
                result = Math.Min(ceiling, result + next[pathLength]);
            }
            if (result > maximumMultiplicity)
            {
                throw new PathMultiplicityBudgetException("Path decomposition exceeds its result multiplicity budget.");
            }
            (previous, next) = (next, previous);
            if (!any)
            {
                break;
            }
        }
        if (result > maximumMultiplicity)
        {
            throw new PathMultiplicityBudgetException("Path decomposition exceeds its result multiplicity budget.");
        }
        return (int)result;
    }
}
