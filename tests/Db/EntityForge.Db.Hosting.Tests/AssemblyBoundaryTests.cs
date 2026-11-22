using EntityForge.Db.Transport.Contracts;
using System.Reflection;
using System.Xml.Linq;
using EntityForge.Db.Client;
using EntityForge.Db.Distributed.Hosting;
using EntityForge.Db.Distributed.Sharding;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Query;
using EntityForge.Query.Projection;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class AssemblyBoundaryTests
{
    [TestMethod]
    public void Reusable_query_and_distributed_assemblies_do_not_load_a_web_host()
    {
        Assembly[] roots = [typeof(SemanticNativeQueryView).Assembly,
            typeof(PortableEntityQueryRequestCompiler).Assembly, typeof(PartitionApplicationClient).Assembly,
            typeof(HttpPartitionEntityClient).Assembly, typeof(LogicalShardId).Assembly];
        foreach (var root in roots)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<Assembly>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var assembly))
            {
                if (!seen.Add(assembly.GetName().Name!))
                {
                    continue;
                }

                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    string name = reference.Name!;
                    Assert.IsFalse(name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                        name is "EntityForge.Db.Hosting" or "EntityForge.Db.Distributed.Hosting" or "EntityForge.Db.Server",
                        $"{root.GetName().Name} reaches {name} through {assembly.GetName().Name}.");
                    if (name.StartsWith("EntityForge.", StringComparison.Ordinal))
                    {
                        queue.Enqueue(Assembly.Load(reference));
                    }
                }
            }
        }
    }

    [TestMethod]
    public void Production_project_graph_has_no_cycles()
    {
        string root = RepositoryTestPaths.FindRoot();
        var graph = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(Path.GetFullPath, path => XDocument.Load(path).Descendants("ProjectReference")
                .Select(item => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, (string)item.Attribute("Include")!))).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string project)
        {
            Assert.IsFalse(active.Contains(project), $"Circular project dependency at {project}.");
            if (!visited.Add(project))
            {
                return;
            }

            active.Add(project);
            foreach (string dependency in graph.GetValueOrDefault(project, []))
            {
                Visit(dependency);
            }

            active.Remove(project);
        }

        foreach (string project in graph.Keys)
        {
            Visit(project);
        }
    }

    [TestMethod]
    [DataRow(DatabaseProfileId.PropertyGraph)]
    [DataRow(DatabaseProfileId.Rdf)]
    public async Task Graphql_resolvers_execute_against_application_services_without_host_runtime(DatabaseProfileId profile)
    {
        var application = new ProjectionApplication(profile);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IGraphQlApplicationServices>(application);
        ProfileGraphQlRuntime.Register(services);
        await using var provider = services.BuildServiceProvider();
        Assert.IsNull(provider.GetService<EntityForgeDbRuntime>());
        var executor = await provider.GetRequiredService<IRequestExecutorResolver>()
            .GetRequestExecutorAsync(ProfileGraphQlRuntime.SchemaName(profile));
        await using var result = await executor.ExecuteAsync(OperationRequestBuilder.New()
            .SetDocument("{ entities(type: \"urn:test:Thing\", first: 2) { nodes { identity } pageInfo { snapshotVersion } } }")
            .Build());
        Assert.IsInstanceOfType<IOperationResult>(result);
        var operation = (IOperationResult)result;
        Assert.IsTrue(operation.Errors is null || operation.Errors.Count == 0,
            String.Join("; ", operation.Errors?.Select(error => error.Message) ?? []));
        Assert.AreEqual(1, application.Calls);
        Assert.AreEqual(2, application.Request!.First);
        Assert.IsNotNull(operation.Data);
    }

    private sealed class ProjectionApplication(DatabaseProfileId profile) : IGraphQlApplicationServices
    {
        public DatabaseProfileId Profile => profile;

        public ISemanticDatabase Semantic => throw new NotSupportedException();

        internal int Calls
        {
            get; private set;
        }

        internal EntityProjectionQueryRequest? Request
        {
            get; private set;
        }

        public ValueTask<EntityProjectionQueryResult> QueryAsync(EntityProjectionQueryRequest request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Calls++;
            Request = request;
            return ValueTask.FromResult(new EntityProjectionQueryResult(new EntityProjection(Guid.NewGuid(), []), new DatabaseVersion(7)));
        }

        public ValueTask<DatabaseCommitReceipt> CommitGraphAsync(GraphTransaction transaction, CancellationToken cancellation) =>
            throw new NotSupportedException();

        public ValueTask<DatabaseCommitReceipt> CommitRdfAsync(RdfTransaction transaction, CancellationToken cancellation) =>
            throw new NotSupportedException();
    }
}
