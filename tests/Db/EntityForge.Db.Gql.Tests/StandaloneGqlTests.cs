using System.Reflection;
using System.Text.Json;
using EntityForge.Db.Graph;
using EntityForge.Query.Results;

namespace EntityForge.Db.Gql.Tests;

[TestClass]
public sealed class StandaloneGqlTests
{
    [TestMethod]
    public void Gql_dependency_closure_excludes_host_and_web_framework_assemblies()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Assembly>();
        pending.Push(typeof(GraphGqlQueryRequest).Assembly);
        while (pending.TryPop(out var assembly))
        {
            if (!visited.Add(assembly.GetName().Name!))
            {
                continue;
            }
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                string name = reference.Name!;
                Assert.IsFalse(name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                    name is "EntityForge.Db.Hosting" or "EntityForge.Db.Server" or "EntityForge.Db.Distributed",
                    $"GQL dependency '{assembly.GetName().Name}' reaches host infrastructure '{name}'.");
                if (name.StartsWith("EntityForge.", StringComparison.Ordinal))
                {
                    pending.Push(Assembly.Load(reference));
                }
            }
        }
    }

    [TestMethod]
    public async Task Standard_Gql_mutations_and_parameterized_reads_execute_without_a_host()
    {
        string directory = Path.Combine(Path.GetTempPath(), "entityforge-gql-isolation-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var database = await GraphDatabase.OpenLocalAsync(new(directory));
            string statement = GraphGqlStandardSyntax.PrepareParameterized(
                "INSERT (a:Person {name: 'Ada', age: 42}), (b:Person {name: 'Ben', age: 12})", null, out _);
            Guid transactionId = Guid.NewGuid();
            var mutation = GraphGqlMutationCompiler.Compile(statement, transactionId: transactionId);
            await database.CommitAsync(new(transactionId, GraphGqlMutationCompiler.BindDirect(mutation)));
            var request = new GraphGqlQueryRequest("MATCH (n:Person) WHERE n.age >= $minimum RETURN n",
                Parameters: new Dictionary<string, JsonElement> { ["minimum"] = JsonSerializer.SerializeToElement(18) });
            var query = new GraphGqlQueryCompiler().CompileStandard(request);
            var result = await query.ExecuteAsync(plan => database.QueryAsync(plan.Query, plan.SnapshotVersion is long version ? new DatabaseVersion(version) : null), default);
            Assert.IsInstanceOfType<GraphPatternResult>(result.Result);
            Assert.AreEqual(1, ((GraphPatternResult)result.Result).Matches.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
