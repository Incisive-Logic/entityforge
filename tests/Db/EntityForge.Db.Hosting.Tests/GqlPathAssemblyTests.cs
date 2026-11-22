using EntityForge.Db.Graph;
using EntityForge.Query.Values;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GqlPathAssemblyTests
{
    private static readonly NodeValue A = new(new IdentifierValue("a"), labels: ["Start"]);
    private static readonly NodeValue B = new(new IdentifierValue("b"), labels: ["Middle"]);
    private static readonly NodeValue C = new(new IdentifierValue("c"), labels: ["End"]);

    private static EdgeValue Edge(NodeValue source, NodeValue target, string name) =>
        new(source.Identity, target.Identity, new SemanticTerm(new Uri("urn:link"), SemanticTermRole.Relationship), new IdentifierValue(name));

    private static GraphScalarEvaluator.IterationBudget Budget() => new()
    {
        ResolveNode = identity => new[] { A, B, C }.Single(node => node.Identity == identity)
    };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Ordered_path_assembly_retains_direction_and_complete_nodes(bool reverse)
    {
        var first = Edge(A, B, "ab");
        var second = Edge(C, B, "cb");
        var edges = reverse ? new[] { second, first } : new[] { first, second };
        var result = (PathValue)GraphScalarEvaluator.AssemblePatternPath(reverse ? C : A, new ListValue(edges), Budget());
        QueryValue[] expected = reverse ? [C, second, B, first, A] : [A, first, B, second, C];
        CollectionAssert.AreEqual(expected, result.Elements.ToArray());
        Assert.AreSame(B, result.Elements[2]);
        CollectionAssert.AreEqual(new[] { "Middle" }, ((NodeValue)result.Elements[2]).Labels.ToArray());
    }

    [TestMethod]
    public void Ordered_path_assembly_preserves_repetition_and_self_loops()
    {
        var edge = Edge(A, B, "ab");
        var loop = Edge(A, A, "loop");
        long charged = 0;
        var budget = new GraphScalarEvaluator.IterationBudget { ResolveNode = id => id == A.Identity ? A : B, ChargePathWork = work => charged += work };
        var result = (PathValue)GraphScalarEvaluator.AssemblePatternPath(A, new ListValue([edge, edge, loop]), budget);
        CollectionAssert.AreEqual(new QueryValue[] { A, edge, B, edge, A, loop, A }, result.Elements.ToArray());
        Assert.AreEqual(3L, charged);
    }

    [TestMethod]
    public void Ordered_path_assembly_preserves_empty_and_absent_values()
    {
        var result = (PathValue)GraphScalarEvaluator.AssemblePatternPath(A, new ListValue([]), new());
        CollectionAssert.AreEqual(new QueryValue[] { A }, result.Elements.ToArray());
        Assert.AreSame(UnboundValue.Instance, GraphScalarEvaluator.AssemblePatternPath(UnboundValue.Instance, new ListValue([]), new()));
    }

    [TestMethod]
    public void Ordered_path_assembly_rejects_disconnected_and_missing_endpoints()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.AssemblePatternPath(A, new ListValue([Edge(B, C, "bc")]), Budget()));
        Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.AssemblePatternPath(A, new ListValue([Edge(A, B, "ab")]), new()));
        Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.AssemblePatternPath(A, new ListValue([Edge(A, B, "ab")]), new() { ResolveNode = _ => C }));
        Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.AssemblePatternPath(A, new ListValue([new LiteralValue("bad")]), Budget()));
    }

    [TestMethod]
    public void Ordered_path_assembly_enforces_capacity_cancellation_and_work_budget()
    {
        var loop = Edge(A, A, "loop");
        Assert.ThrowsExactly<ArgumentException>(() => GraphScalarEvaluator.AssemblePatternPath(A, new ListValue(Enumerable.Repeat(loop, 5_000)), Budget()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => GraphScalarEvaluator.AssemblePatternPath(A, new ListValue([]), new() { Cancellation = cancellation.Token }));
        Assert.ThrowsExactly<InvalidOperationException>(() => GraphScalarEvaluator.AssemblePatternPath(A, new ListValue([loop]),
            new()
            {
                ChargePathWork = _ => throw new InvalidOperationException("Budget exhausted")
            }));
    }
}
