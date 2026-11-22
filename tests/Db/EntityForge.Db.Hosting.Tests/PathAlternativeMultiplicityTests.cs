using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class PathAlternativeMultiplicityTests
{
    [TestMethod]
    public void Path_alternative_counts_match_independent_sequence_enumeration()
    {
        var random = new Random(19375);
        for (int sample = 0; sample < 100; sample++)
        {
            int length = random.Next(0, 8);
            var matches = new List<PathAlternativeMatch>();
            for (int start = 0; start < length; start++)
            {
                for (int end = start + 1; end <= length; end++)
                {
                    if (random.Next(3) == 0)
                    {
                        matches.Add(new(start, end, random.Next(0, 4)));
                    }
                }
            }
            for (int minimum = 0; minimum <= length + 1; minimum++)
            {
                int Enumerate(int position, int iterations) => position == length
                    ? iterations >= minimum ? 1 : 0
                    : matches.Where(match => match.Start == position)
                        .Sum(match => match.Multiplicity * Enumerate(match.End, iterations + 1));
                Assert.AreEqual(Enumerate(0, 0), PathAlternativeMultiplicity.Count(length, minimum, null,
                    matches, 100, 100, 100_000, 100_000), $"sample={sample}, minimum={minimum}");
            }
        }
    }

    [TestMethod]
    public void Path_alternative_counts_preserve_duplicate_transitions_and_iteration_bounds()
    {
        PathAlternativeMatch[] matches = [new(0, 1, 2), new(1, 2, 3), new(0, 2, 1), new(0, 2, 1)];
        Assert.AreEqual(8, PathAlternativeMultiplicity.Count(2, 1, null, matches, 10, 10, 100, 100));
        Assert.AreEqual(2, PathAlternativeMultiplicity.Count(2, 1, 1, matches, 10, 10, 100, 100));
        Assert.AreEqual(6, PathAlternativeMultiplicity.Count(2, 2, 2, matches, 10, 10, 100, 100));
        Assert.AreEqual(1, PathAlternativeMultiplicity.Count(0, 0, null, [], 1, 0, 1, 0));
        Assert.AreEqual(0, PathAlternativeMultiplicity.Count(0, 1, null, [], 1, 0, 1, 0));
    }

    [TestMethod]
    public void Path_alternative_counts_bound_resources_and_do_not_overflow_dead_ends()
    {
        PathAlternativeMatch[] dead = [new(0, 1, int.MaxValue), new(1, 2, int.MaxValue), new(0, 3, 1)];
        Assert.AreEqual(1, PathAlternativeMultiplicity.Count(3, 1, null, dead, 10, 10, 1, 100));
        Assert.Throws<PathMultiplicityBudgetException>(() => PathAlternativeMultiplicity.Count(3, 1, null, dead, 3, 10, 1, 100));
        Assert.Throws<PathMultiplicityBudgetException>(() => PathAlternativeMultiplicity.Count(3, 1, null, dead, 10, 2, 1, 100));
        Assert.Throws<PathMultiplicityBudgetException>(() => PathAlternativeMultiplicity.Count(3, 1, null, dead, 10, 10, 1, 2));
        Assert.Throws<PathMultiplicityBudgetException>(() => PathAlternativeMultiplicity.Count(2, 1, null,
            [new(0, 1, int.MaxValue), new(1, 2, int.MaxValue)], 10, 10, int.MaxValue, 100));
        Assert.Throws<PathMultiplicityBudgetException>(() => PathAlternativeMultiplicity.Count(0, 0, null, [], 1, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => PathAlternativeMultiplicity.Count(1, 0, null, [new(0, 0, 1)], 10, 10, 10, 100));
        Assert.Throws<OperationCanceledException>(() => PathAlternativeMultiplicity.Count(0, 0, null, [], 1, 0, 1, 0,
            new CancellationToken(canceled: true)));
        Assert.AreEqual(1, PathAlternativeMultiplicity.Count(1, 1, null, [new(0, 1, 1)], 10, 10, 10, 100));
    }
}
