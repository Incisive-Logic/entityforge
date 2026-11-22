using System.Reflection;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class NativeApplicationContractArchitectureTests
{
    [TestMethod]
    public void Native_application_contracts_do_not_depend_on_embedded_execution()
    {
        Type[] graph = [typeof(IGraphEntityClient),
            typeof(IGraphEntityReadScope),
            typeof(GraphEntityRequest),
            typeof(GraphEntityWrite),
            typeof(GraphChangeSession)];
        Type[] rdf = [typeof(IRdfEntityClient),
            typeof(IRdfEntityReadScope),
            typeof(RdfEntityRequest),
            typeof(RdfEntityWrite),
            typeof(RdfChangeSession)];
        foreach (var type in graph)
        {
            Assert.AreEqual("EntityForge.Db.Graph.Contracts", type.Assembly.GetName().Name);
        }

        foreach (var type in rdf)
        {
            Assert.AreEqual("EntityForge.Db.Rdf.Contracts", type.Assembly.GetName().Name);
        }

        Assert.AreEqual("EntityForge.Db.Semantic.Client", typeof(SemanticEntityStore<>).Assembly.GetName().Name);
        Assert.AreEqual(typeof(SemanticEntityStore<>).Assembly, typeof(ISemanticEntityClient).Assembly);
        var visited = new HashSet<string>();
        Assert.AreEqual("EntityForge.Db.Graph.Client", typeof(GraphEntityStore<>).Assembly.GetName().Name);
        Assert.AreEqual("EntityForge.Db.Rdf.Client", typeof(RdfEntityStore<>).Assembly.GetName().Name);
        var pending = new Queue<Assembly>(graph.Concat(rdf).Select(t => t.Assembly).Concat([typeof(GraphEntityStore<>).Assembly, typeof(RdfEntityStore<>).Assembly, typeof(SemanticEntityStore<>).Assembly]).Distinct());
        while (pending.TryDequeue(out var assembly))
        {
            if (!visited.Add(assembly.FullName!))
            {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name!;
                Assert.IsFalse(name is "EntityForge.Db.Graph" or "EntityForge.Db.Rdf" or "EntityForge.Db.Semantic" or "EntityForge.Db.Semantic.Engine" or "EntityForge.Db.Kernel"
                    || name.StartsWith("EntityForge.Db.Storage", StringComparison.Ordinal)
                    || name.StartsWith("EntityForge.Db.Prototypes", StringComparison.Ordinal),
                    $"Client contract dependency {assembly.GetName().Name} -> {name} includes embedded execution.");
                if (name.StartsWith("EntityForge.", StringComparison.Ordinal))
                {
                    pending.Enqueue(Assembly.Load(reference));
                }
            }
        }
    }
}
