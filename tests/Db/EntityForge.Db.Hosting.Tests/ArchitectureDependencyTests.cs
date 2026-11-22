using System.Xml.Linq;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Semantic;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class ArchitectureDependencyTests
{
    private static readonly string[] NeutralProjects =
    ["EntityForge.Db.Mapping.Runtime",
        "EntityForge.Db.AccessPaths",
        "EntityForge.Db.Abstractions",
        "EntityForge.Db.Client",
        "EntityForge.Db.Durability.Abstractions",
        "EntityForge.Db.Kernel",
        "EntityForge.Db.Query.Planning",
        "EntityForge.Db.Query.Syntax",
        "EntityForge.Db.Storage",
        "EntityForge.Db.Storage.Local",
        "EntityForge.Db.Storage.Composition",
        "EntityForge.Db.Storage.Pages",
        "EntityForge.Db.Routing"];

    private static readonly string[] ProfileMarkers =
        ["EntityForge.Db.Graph", "EntityForge.Db.Rdf", "EntityForge.Db.Semantic"];

    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["EntityForge.Db.AccessPaths"] = [],
            ["EntityForge.Db.Distributed.Contracts"] = ["EntityForge.Db.Abstractions"],
            ["EntityForge.Db.Mapping.Runtime"] = ["EntityForge.Db.Abstractions"],
            ["EntityForge.Db.Abstractions"] = ["EntityForge.Values"],
            ["EntityForge.Db.Client"] = ["EntityForge.Db.Abstractions",
                "EntityForge.Db.Query.Planning",
                "EntityForge.Db.Transport.Contracts",
                "EntityForge.Query",
                "EntityForge.Semantics"],
            ["EntityForge.Db.Durability.Abstractions"] = ["EntityForge.Db.Abstractions"],
            ["EntityForge.Db.Storage"] = ["EntityForge.Db.Abstractions", "EntityForge.Db.Durability.Abstractions"],
            ["EntityForge.Db.Storage.Local"] = ["EntityForge.Db.Abstractions",
                "EntityForge.Db.Durability.Abstractions",
                "EntityForge.Db.Storage"],
            ["EntityForge.Db.Storage.Composition"] = ["EntityForge.Db.Storage.Local",
                "EntityForge.Db.Storage.Pages"],
            ["EntityForge.Db.Storage.Pages"] = ["EntityForge.Db.Abstractions",
                "EntityForge.Db.AccessPaths",
                "EntityForge.Db.Durability.Abstractions",
                "EntityForge.Db.Storage"],
            ["EntityForge.Db.Kernel"] = ["EntityForge.Db.Mapping.Runtime",
                "EntityForge.Db.Abstractions",
                "EntityForge.Db.Durability.Abstractions",
                "EntityForge.Db.Storage"],
            ["EntityForge.Db.Query.Planning"] = [],
            ["EntityForge.Db.Query.Syntax"] = [],
            ["EntityForge.Db.Gql"] = ["EntityForge.Db.Graph", "EntityForge.Db.Graph.Contracts",
                "EntityForge.Db.Query.Syntax", "EntityForge.Db.Query.Planning", "EntityForge.Db.Mapping.Runtime",
                "EntityForge.Db.Abstractions", "EntityForge.Query"],
            ["EntityForge.Db.Routing"] = ["EntityForge.Db.Abstractions"],
            ["EntityForge.Db.Graph.Contracts"] = ["EntityForge.Db.Abstractions"],
            ["EntityForge.Db.Graph.Client"] = ["EntityForge.Db.Graph.Contracts", "EntityForge.Db.Client", "EntityForge.Query"],
            ["EntityForge.Db.Graph"] = ["EntityForge.Db.Mapping.Runtime",
                "EntityForge.Db.Graph.Client",
                "EntityForge.Db.AccessPaths",
                "EntityForge.Db.Graph.Contracts",
                "EntityForge.Db.Kernel",
                "EntityForge.Db.Abstractions",
                "EntityForge.Db.Storage",
                "EntityForge.Db.Storage.Local",
                "EntityForge.Db.Storage.Composition",
                "EntityForge.Db.Storage.Pages",
                "EntityForge.Db.Query.Planning",
                "EntityForge.Query",
                "EntityForge.Semantics",
                "EntityForge.Db.Routing",
                "EntityForge.Db.Client",
                "EntityForge.Db.Transport.Contracts"],
            ["EntityForge.Db.Rdf.Contracts"] = ["EntityForge.Db.Abstractions"],
            ["EntityForge.Db.Rdf.Client"] = ["EntityForge.Db.Rdf.Contracts", "EntityForge.Db.Client", "EntityForge.Query"],
            ["EntityForge.Db.Rdf"] = ["EntityForge.Db.Mapping.Runtime",
                "EntityForge.Db.Rdf.Client",
                "EntityForge.Db.Rdf.Contracts",
                "EntityForge.Db.Kernel",
                "EntityForge.Db.Storage",
                "EntityForge.Db.Storage.Local",
                "EntityForge.Db.Storage.Composition",
                "EntityForge.Db.Storage.Pages",
                "EntityForge.Db.Query.Planning",
                "EntityForge.Query",
                "EntityForge.Semantics",
                "EntityForge.Db.Client",
                "EntityForge.Db.Transport.Contracts"],
            // Delivery framing carries conservative owner-coverage contracts, not a durability implementation.
            ["EntityForge.Db.Semantic.Client"] = ["EntityForge.Db.Semantic.Durability.Abstractions", "EntityForge.Db.Mapping.Runtime", "EntityForge.Db.Semantic.Abstractions", "EntityForge.Semantics.Protobuf", "EntityForge.Db.Client"],
            ["EntityForge.Db.Semantic.Abstractions"] = ["EntityForge.Db.Abstractions",
                "EntityForge.Query",
                "EntityForge.Semantics"],
            ["EntityForge.Db.Semantic.Durability.Abstractions"] = ["EntityForge.Db.Abstractions",
                "EntityForge.Db.Semantic.Abstractions"],
            ["EntityForge.Db.Semantic"] = ["EntityForge.Db.Mapping.Runtime",
                "EntityForge.Db.Semantic.Client",
                "EntityForge.Db.Semantic.Abstractions",
                "EntityForge.Db.Abstractions",
                "EntityForge.Db.Durability.Abstractions",
                "EntityForge.Db.Semantic.Durability.Abstractions",
                "EntityForge.Db.Kernel",
                "EntityForge.Db.Storage",
                "EntityForge.Db.Storage.Local",
                "EntityForge.Db.Storage.Composition",
                "EntityForge.Db.Storage.Pages",
                "EntityForge.Db.Semantic.Engine",
                "EntityForge.Db.Routing",
                "EntityForge.Db.Telemetry",
                "EntityForge.Semantics.Protobuf",
                "EntityForge.Db.Client",
                "EntityForge.Db.Transport.Contracts"]
        };

    [TestMethod]
    public void Neutral_kernel_and_storage_projects_have_no_profile_dependencies()
    {
        string root = RepositoryTestPaths.FindRoot();
        Dictionary<string, string[]> references = Directory.GetFiles(Path.Combine(root, "src", "Db"), "*.csproj",
                SearchOption.AllDirectories).ToDictionary(
                    path => ProjectName(path),
                    ReadReferences);

        foreach (string project in NeutralProjects)
        {
            var reachable = new HashSet<string>(StringComparer.Ordinal);
            Visit(project, references, reachable);
            string[] forbidden = reachable.Where(item => ProfileMarkers.Any(marker =>
                item.Contains(marker, StringComparison.Ordinal))).ToArray();
            Assert.AreEqual(0, forbidden.Length,
                $"Neutral project '{project}' reaches profile projects: {String.Join(", ", forbidden)}.");
        }
    }

    [TestMethod]
    public void Foundational_and_profile_projects_use_only_explicitly_allowed_dependencies()
    {
        string root = RepositoryTestPaths.FindRoot();
        Dictionary<string, string[]> references = Directory.GetFiles(Path.Combine(root, "src", "Db"), "*.csproj",
                SearchOption.AllDirectories).ToDictionary(
                    path => ProjectName(path), ReadReferences);
        foreach ((string project, string[] allowed) in AllowedReferences)
        {
            string[] unexpected = references[project].Except(allowed, StringComparer.Ordinal).ToArray();
            Assert.AreEqual(0, unexpected.Length,
                $"Project '{project}' has unexpected references: {String.Join(", ", unexpected)}.");
        }
    }

    [TestMethod]
    public void Gql_syntax_assembly_does_not_depend_on_database_or_HTTP_execution()
    {
        var references = typeof(EntityForge.Db.Query.Syntax.Gql.GqlSyntaxParser).Assembly.GetReferencedAssemblies();
        string[] forbidden = references.Select(reference => reference.Name ?? String.Empty)
            .Where(name => name.StartsWith("EntityForge.", StringComparison.Ordinal) ||
                name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                name.StartsWith("System.Net.Http", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(0, forbidden.Length,
            $"Reusable GQL syntax reaches execution assemblies: {String.Join(", ", forbidden)}.");
    }

    [TestMethod]
    public void DotNetRdf_dependency_is_confined_to_the_RDF_interoperability_profile()
    {
        string root = RepositoryTestPaths.FindRoot();
        string[] owners = Directory.GetFiles(Path.Combine(root, "src", "Db"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => XDocument.Load(path).Descendants("PackageReference").Any(reference =>
                reference.Attribute("Include")?.Value.StartsWith("dotNetRdf", StringComparison.OrdinalIgnoreCase) is true))
            .Select(ProjectName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "EntityForge.Db.Rdf" }, owners);
    }

    [TestMethod]
    public void Kernel_and_page_storage_do_not_expose_internals_to_production_profiles()
    {
        string root = RepositoryTestPaths.FindRoot();
        foreach (string project in new[] { "EntityForge.Db.Kernel", "EntityForge.Db.Storage.Pages" })
        {
            string path = Directory.GetFiles(Path.Combine(root, "src", "Db", project), "*.csproj").Single();
            string[] productionFriends = XDocument.Load(path).Descendants("InternalsVisibleTo")
                .Select(item => item.Attribute("Include")?.Value ?? String.Empty)
                .Where(assembly => !assembly.EndsWith(".Tests", StringComparison.Ordinal))
                .ToArray();
            Assert.AreEqual(0, productionFriends.Length,
                $"Neutral project '{project}' exposes internals to: {String.Join(", ", productionFriends)}.");
        }
    }

    [TestMethod]
    public void Ordinary_profile_interfaces_do_not_expose_kernel_types()
    {
        Type[] contracts = [typeof(IGraphDatabase), typeof(IRdfDatabase), typeof(ISemanticDatabase)];
        Type[] exposed = contracts.SelectMany(contract => contract.GetInterfaces().Append(contract))
            .SelectMany(contract => contract.GetMembers())
            .SelectMany(MemberTypes)
            .Distinct()
            .Where(type => type.Namespace?.StartsWith("EntityForge.Db.Kernel", StringComparison.Ordinal) is true)
            .ToArray();

        Assert.AreEqual(0, exposed.Length,
            $"Ordinary profile contracts expose kernel types: {String.Join(", ", exposed.Select(type => type.FullName))}.");
    }

    private static IEnumerable<Type> MemberTypes(System.Reflection.MemberInfo member) => member switch
    {
        System.Reflection.MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .Append(method.ReturnType),
        System.Reflection.PropertyInfo property => [property.PropertyType],
        _ => []
    };

    private static void Visit(string project, IReadOnlyDictionary<string, string[]> graph, ISet<string> reached)
    {
        if (!graph.TryGetValue(project, out string[]? dependencies))
        {
            return;
        }

        foreach (string dependency in dependencies)
        {
            if (reached.Add(dependency))
            {
                Visit(dependency, graph, reached);
            }
        }
    }

    private static string[] ReadReferences(string projectPath) => XDocument.Load(projectPath)
        .Descendants("ProjectReference").Select(item =>
            ProjectName(item.Attribute("Include")!.Value)).ToArray();

    private static string ProjectName(string path)
    {
        string normalized = path.Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFileNameWithoutExtension(normalized) ??
               throw new InvalidDataException("A project path has no filename.");
    }
}
