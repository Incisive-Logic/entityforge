namespace EntityForge.Db.Query.Planning;

internal static partial class PathAlternativeMultiplicity
{
    // Return transition indices, leaving profile values and ordered group
    // binding construction with the caller. No recursive call stack is used.
    internal static IReadOnlyList<int[]> Enumerate(int pathLength, int minimumIterations, int? maximumIterations,
        IReadOnlyList<PathAlternativeMatch> matches, int maximumPositions, int maximumTransitions,
        int maximumMultiplicity, long maximumWork, CancellationToken cancellationToken = default,
        Action<long>? chargeWork = null)
    {
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
        int count = Count(pathLength, minimumIterations, maximumIterations, matches, maximumPositions,
            maximumTransitions, maximumMultiplicity, maximumWork, cancellationToken, Charge);
        if (count == 0)
        {
            return [];
        }
        Charge((long)(pathLength + 1) * 4);
        var outgoing = new List<int>?[pathLength + 1];
        for (int index = 0; index < matches.Count; index++)
        {
            Charge();
            if (matches[index].Multiplicity != 0)
            {
                (outgoing[matches[index].Start] ??= []).Add(index);
            }
        }
        var reachesEnd = new bool[pathLength + 1];
        reachesEnd[pathLength] = true;
        for (int position = pathLength - 1; position >= 0; position--)
        {
            if (outgoing[position] is not { } choices)
            {
                continue;
            }
            foreach (int index in choices)
            {
                Charge();
                reachesEnd[position] |= reachesEnd[matches[index].End];
            }
        }
        int maximum = Math.Min(maximumIterations ?? pathLength, pathLength);
        var frames = new DecompositionFrame[pathLength + 1];
        var selected = new int[pathLength];
        var result = new List<int[]>(count);
        int depth = 0;
        while (depth >= 0)
        {
            Charge();
            ref var frame = ref frames[depth];
            if (frame.Position == pathLength)
            {
                if (depth >= minimumIterations)
                {
                    Charge(depth + 1L);
                    result.Add(selected[..depth]);
                }
                depth--;
                continue;
            }
            if (depth == maximum || outgoing[frame.Position] is not { } choices || frame.Next >= choices.Count)
            {
                depth--;
                continue;
            }
            int index = choices[frame.Next];
            var match = matches[index];
            if (!reachesEnd[match.End] || frame.Copy == match.Multiplicity)
            {
                frame.Next++;
                frame.Copy = 0;
                continue;
            }
            frame.Copy++;
            selected[depth] = index;
            frames[++depth] = new()
            {
                Position = match.End
            };
        }
        return result;
    }

    private struct DecompositionFrame
    {
        internal int Position;
        internal int Next;
        internal int Copy;
    }
}
