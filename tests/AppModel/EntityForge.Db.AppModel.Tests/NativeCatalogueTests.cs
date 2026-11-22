using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class NativeCatalogueTests
{
    [TestMethod]
    public void GraphVocabularyDoesNotGenerateQueriesForLabelsWithTrailingNewlines()
    {
        using var json = JsonDocument.Parse("""
        {"nodes":[{"labels":["Person\n"],"properties":{}}],"edges":[]}
        """);
        Assert.IsNull(NativeCatalogueDiscovery.FromGraph(json.RootElement).Single().Query);
    }

    [TestMethod]
    public void GraphVocabularyIncludesEdgePropertiesAndRejectsUnsafeQueryIdentifiers()
    {
        using var json = JsonDocument.Parse("""
        {"nodes":[{"labels":["Person","odd) MATCH (x"],"properties":{"name":"Ada"}}],
        "edges":[{"relationship":"knows","properties":{"since":2020}}]}
        """);
        var entries = NativeCatalogueDiscovery.FromGraph(json.RootElement).ToArray();
        Assert.IsTrue(entries.Any(e => e.Category == "Relationship properties" && e.Name == "since"));
        Assert.IsNull(entries.Single(e => e.Name.StartsWith("odd")).Query);
        StringAssert.Contains(entries.Single(e => e.Name == "knows").Query!, "[e:`knows`]");
    }

    [TestMethod]
    public void RdfQueriesPreserveGraphScopeAndRejectInjection()
    {
        StringAssert.Contains(NativeCatalogueDiscovery.RdfBrowseQuery("Predicates", "urn:name")!, "UNION");
        StringAssert.Contains(NativeCatalogueDiscovery.RdfBrowseQuery("Named graphs", "urn:g")!, "GRAPH <urn:g>");
        Assert.IsNull(NativeCatalogueDiscovery.RdfBrowseQuery("Predicates", "urn:x> } UNION { ?s ?p ?o"));
    }

    [TestMethod]
    public async Task RebindingDropsLateDiscovery()
    {
        var executor = new Deferred();
        using var session = new NativeCatalogueSession();
        session.Bind(executor);
        var pending = session.RefreshAsync();
        session.Bind(null);
        using var document = JsonDocument.Parse("{\"bindings\":[]}");
        executor.Completion.SetResult(new(NativeQueryLanguage.Sparql, document.RootElement.Clone()));
        await pending;
        Assert.IsNull(session.Report);
        Assert.IsNull(session.Error);
        Assert.IsFalse(session.IsLoading);
    }

    private sealed class Deferred : INativeQueryExecutor
    {
        public NativeQueryLanguage Language => NativeQueryLanguage.Sparql;

        public TaskCompletionSource<NativeQueryResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<NativeQueryResult> ExecuteAsync(string query, CancellationToken cancellationToken = default) => new(Completion.Task);
    }
}
