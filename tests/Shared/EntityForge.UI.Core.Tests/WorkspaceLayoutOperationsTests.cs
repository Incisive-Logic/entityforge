using EntityForge.UI.Workspace;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

namespace EntityForge.UI.Core.Tests;

[TestClass]
public sealed class WorkspaceLayoutOperationsTests
{
    [TestMethod]
    public void ResizeReturnsNewTreeAndBoundsProportion()
    {
        WorkspaceLayout original = TwoModuleLayout();

        WorkspaceLayout resized = WorkspaceLayoutOperations.Resize(original, "root", 0.99);

        Assert.AreEqual(0.5, ((WorkspaceSplitNode)original.Root).FirstProportion);
        Assert.AreEqual(WorkspaceLayoutOperations.MaximumProportion, ((WorkspaceSplitNode)resized.Root).FirstProportion);
    }

    [TestMethod]
    public void SplitThenRemoveRestoresSurvivingModule()
    {
        var original = new WorkspaceLayout(new WorkspaceModuleNode("one", "module.one"));
        WorkspaceLayout split = WorkspaceLayoutOperations.Split(
            original,
            "one",
            new WorkspaceModuleNode("two", "module.two"),
            WorkspaceSplitOrientation.Vertical,
            WorkspacePlacement.After,
            splitId: "root");

        WorkspaceLayout removed = WorkspaceLayoutOperations.RemoveModule(split, "two");

        Assert.AreEqual(original, removed);
    }

    [TestMethod]
    public void SwapPreservesSlotsAndExchangesModuleKeys()
    {
        WorkspaceLayout swapped = WorkspaceLayoutOperations.SwapModules(TwoModuleLayout(), "one", "two");
        var root = (WorkspaceSplitNode)swapped.Root;

        Assert.AreEqual("module.two", ((WorkspaceModuleNode)root.First).ModuleKey);
        Assert.AreEqual("module.one", ((WorkspaceModuleNode)root.Second).ModuleKey);
        Assert.AreEqual("one", root.First.Id);
        Assert.AreEqual("two", root.Second.Id);
    }

    [TestMethod]
    public void ReplaceModulePreservesStableInstanceId()
    {
        WorkspaceLayout replaced = WorkspaceLayoutOperations.ReplaceModule(TwoModuleLayout(), "one", "module.replacement");
        var module = (WorkspaceModuleNode)((WorkspaceSplitNode)replaced.Root).First;

        Assert.AreEqual("one", module.Id);
        Assert.AreEqual("module.replacement", module.ModuleKey);
    }

    [TestMethod]
    public void DuplicateNodeIdsAreRejected()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new WorkspaceLayout(
            new WorkspaceSplitNode(
                "root",
                WorkspaceSplitOrientation.Horizontal,
                0.5,
                new WorkspaceModuleNode("duplicate", "module.one"),
                new WorkspaceModuleNode("duplicate", "module.two"))));
    }

    [TestMethod]
    public void LayoutRoundTripsWithoutOwningModuleStateDtos()
    {
        WorkspaceLayout original = TwoModuleLayout();

        string json = JsonSerializer.Serialize(original);
        WorkspaceLayout? restored = JsonSerializer.Deserialize<WorkspaceLayout>(json);

        Assert.AreEqual(original, restored);
        StringAssert.Contains(json, "module.one");
        Assert.IsFalse(json.Contains("searchSettings", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CompactOrderDoesNotChangeCanonicalSplitTraversal()
    {
        WorkspaceLayout layout = TwoModuleLayout();
        var preferences = new WorkspaceResponsivePreferences(["two", "one"]);

        IReadOnlyList<WorkspaceModuleNode> compact = WorkspaceLayoutTraversal.OrderForCompactPresentation(layout, preferences);
        IReadOnlyList<WorkspaceModuleNode> canonical = WorkspaceLayoutTraversal.GetModules(layout);

        CollectionAssert.AreEqual(new[] { "two", "one" }, compact.Select(module => module.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "one", "two" }, canonical.Select(module => module.Id).ToArray());
    }

    [TestMethod]
    public void CompactOrderAppendsNewModulesInCanonicalOrder()
    {
        WorkspaceLayout layout = TwoModuleLayout();
        var preferences = new WorkspaceResponsivePreferences(["two"]);

        IReadOnlyList<WorkspaceModuleNode> compact = WorkspaceLayoutTraversal.OrderForCompactPresentation(layout, preferences);

        CollectionAssert.AreEqual(new[] { "two", "one" }, compact.Select(module => module.Id).ToArray());
    }

    private static WorkspaceLayout TwoModuleLayout()
        => new(new WorkspaceSplitNode(
            "root",
            WorkspaceSplitOrientation.Vertical,
            0.5,
            new WorkspaceModuleNode("one", "module.one"),
            new WorkspaceModuleNode("two", "module.two")));
}
