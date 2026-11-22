using EntityForge.Db.Query.Planning;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class PathAlternativeDecompositionTests
{
    [TestMethod]
    public void Path_decompositions_preserve_order_and_weighted_branch_choices()
    {
        var random = new Random(39075);
        for (int sample = 0; sample < 80; sample++)
        {
            int length = random.Next(0, 6);
            var matches = new List<PathAlternativeMatch>();
            for (int start = 0; start < length; start++)
            {
                for (int end = start + 1; end <= length; end++)
                {
                    if (random.Next(3) == 0)
                    {
                        matches.Add(new(start, end, random.Next(0, 3)));
                    }
                }
            }
            int minimum = random.Next(0, length + 2), maximum = random.Next(minimum, length + 3);
            var expected = new List<string>();
            void Visit(int position, List<int> path)
            {
                if (position == length)
                {
                    if (path.Count >= minimum && path.Count <= maximum)
                    {
                        expected.Add(String.Join(',', path));
                    }
                    return;
                }
                if (path.Count == maximum)
                {
                    return;
                }
                for (int index = 0; index < matches.Count; index++)
                {
                    var match = matches[index];
                    if (match.Start != position)
                    {
                        continue;
                    }
                    for (int copy = 0; copy < match.Multiplicity; copy++)
                    {
                        Visit(match.End, [.. path, index]);
                    }
                }
            }
            Visit(0, []);
            var actual = PathAlternativeMultiplicity.Enumerate(length, minimum, maximum, matches, 100, 100, 10_000, 100_000);
            CollectionAssert.AreEquivalent(expected.ToArray(), actual.Select(path => String.Join(',', path)).ToArray(), $"sample={sample}");
        }
    }

    [TestMethod]
    public void Path_decomposition_materialization_is_bounded_and_cancellable()
    {
        PathAlternativeMatch[] matches = [new(0, 1, 2), new(1, 2, 2)];
        Assert.ThrowsExactly<PathMultiplicityBudgetException>(() => PathAlternativeMultiplicity.Enumerate(2, 1, null, matches, 10, 10, 3, 1000));
        Assert.ThrowsExactly<PathMultiplicityBudgetException>(() => PathAlternativeMultiplicity.Enumerate(2, 1, null, matches, 10, 10, 10, 15));
        Assert.ThrowsExactly<OperationCanceledException>(() => PathAlternativeMultiplicity.Enumerate(2, 1, null, matches, 10, 10, 10, 1000, new(true)));
        Assert.AreEqual(4, PathAlternativeMultiplicity.Enumerate(2, 1, null, matches, 10, 10, 10, 1000).Count);
    }
}
