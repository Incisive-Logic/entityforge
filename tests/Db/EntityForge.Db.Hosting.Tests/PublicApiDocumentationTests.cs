using EntityForge.Db.Query.Adapters;
using EntityForge.Db.Gql;
using System.Reflection;
using System.Xml.Linq;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Engine;
using EntityForge.Db.Graph;
using EntityForge.Db.Rdf;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Query.Querying;
using EntityForge.Semantics;
using EntityForge.Values;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class PublicApiDocumentationTests
{
    private static readonly Type[] PrimaryConsumerTypes =
    [
        typeof(EntityForge.Db.Mapping.EntityMappingRegistry),
        typeof(ISemanticDatabase),
        typeof(EntityForgeWriteTransaction),
        typeof(SemanticDatabase),
        typeof(LocalSemanticDatabaseOptions),
        typeof(IGraphDatabase),
        typeof(GraphDatabase),
        typeof(LocalGraphDatabaseOptions),
        typeof(GraphSnapshotLease),
        typeof(EntityForge.Db.Query.Planning.EntityCandidateAccess<>),
        typeof(EntityForge.Db.Query.Planning.EntityCandidatePlanResult<>),
        typeof(EntityForge.Db.Query.Planning.QueryEstimate),
        typeof(EntityForge.Db.Query.Planning.QueryEstimatePolicy),
        typeof(EntityForge.Db.Query.Planning.EntityQueryContinuationCodec),
        typeof(EntityForgeDbServer),
        typeof(EntityForgeDbHostBuilder),
        typeof(EntityForgeDbHost),
        typeof(EntityForgeDbHostOptions),
        typeof(GqlProcedureCatalog),
        typeof(GqlLocalGraphCatalog),
        typeof(GqlReadProcedure),
        typeof(GqlWriteProcedure)
    ];

    private static readonly string[] ImplementationOnlyTypeNames =
    [
        "EntityForge.Db.Semantic.Engine.IEntityForgeCommitObserver",
        "EntityForge.Db.Semantic.Engine.EntityForgeCommitStage",
        "EntityForge.Db.Semantic.Engine.SemanticCommitPreparationSnapshot",
        "EntityForge.Db.Hosting.EntityForgeDbRuntime",
        "EntityForge.Db.Hosting.PortableEntityQueryRequestCompiler",
        "EntityForge.Db.Hosting.PortableEntityQueryResponseMapper"
    ];

    [TestMethod]
    public void Primary_consumer_types_ship_nonempty_XML_documentation()
    {
        foreach (IGrouping<Assembly, Type> assemblyTypes in PrimaryConsumerTypes.GroupBy(type => type.Assembly))
        {
            string documentationPath = Path.ChangeExtension(assemblyTypes.Key.Location, ".xml");
            Assert.IsTrue(File.Exists(documentationPath),
                $"Expected XML documentation beside '{assemblyTypes.Key.GetName().Name}'.");
            XDocument documentation = XDocument.Load(documentationPath);
            HashSet<string> documentedMembers = documentation.Descendants("member")
                .Where(member => !string.IsNullOrWhiteSpace(member.Element("summary")?.Value) ||
                    member.Element("inheritdoc") is not null)
                .Select(member => (string)member.Attribute("name")!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (Type type in assemblyTypes)
            {
                Assert.IsTrue(documentedMembers.Contains($"T:{type.FullName}"),
                    $"Expected useful XML documentation for '{type.FullName}'.");
            }
        }
    }

    [TestMethod]
    public void Composition_and_test_seams_are_not_exported()
    {
        Assembly engine = typeof(EntityForgeWriteTransaction).Assembly;
        Assembly hosting = typeof(EntityForgeDbServer).Assembly;
        Type[] exported = engine.GetExportedTypes().Concat(hosting.GetExportedTypes()).ToArray();

        foreach (string typeName in ImplementationOnlyTypeNames)
        {
            Assert.IsFalse(exported.Any(type => type.FullName == typeName),
                $"Implementation-only type '{typeName}' must not become consumer API.");
        }
    }

    [TestMethod]
    public void Product_facing_package_exports_match_the_reviewed_baseline()
    {
        string baselinePath = Path.Combine(AppContext.BaseDirectory, "PublicApiBaseline.txt");
        string[] expected = File.ReadAllLines(baselinePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assembly[] assemblies =
        [
            typeof(SemanticDatabase).Assembly,
            typeof(HttpEntityProjectionClient).Assembly,
            typeof(HttpSemanticEntityClient).Assembly,
            typeof(GraphDatabase).Assembly,
            typeof(IGraphEntityClient).Assembly,
            typeof(GraphEntityStore<>).Assembly,
            typeof(RdfEntityStore<>).Assembly,
            typeof(IRdfEntityClient).Assembly,
            typeof(RdfDatabase).Assembly,
            typeof(EntityForgeHostCapabilitiesTransport).Assembly,
            typeof(GqlProcedureCatalog).Assembly, typeof(EntityForgeDbServer).Assembly
        ];
        string[] actual = assemblies
            .SelectMany(assembly => assembly.GetExportedTypes().Select(type =>
                $"{assembly.GetName().Name}|{type.FullName}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(expected, actual, Difference(
            "A product-facing package export changed. Review the change and update the baseline intentionally.",
            expected, actual));
    }

    [TestMethod]
    public void Semantic_abstraction_exports_match_the_reviewed_baseline()
    {
        string baselinePath = Path.Combine(AppContext.BaseDirectory, "SemanticPublicApiBaseline.txt");
        string[] expected = File.ReadAllLines(baselinePath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assembly assembly = typeof(ISemanticDatabase).Assembly;
        string[] actual = assembly.GetExportedTypes()
            .Select(type => $"{assembly.GetName().Name}|{type.FullName}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(expected, actual, Difference(
            "The semantic abstraction export changed. Review the change and update the baseline intentionally.",
            expected, actual));
        Assert.IsFalse(actual.Any(item => item.Contains("Durability", StringComparison.Ordinal) ||
            item.EndsWith("SemanticCommittedEffectsCodec", StringComparison.Ordinal)),
            "Semantic persistence or codec implementation leaked into the consumer abstraction package.");
    }

    [TestMethod]
    public void Semantic_database_contract_exposes_no_durability_types()
    {
        Type[] contracts = [typeof(ISemanticDatabase), .. typeof(ISemanticDatabase).GetInterfaces()];
        Type[] exposed = contracts.SelectMany(type => type.GetMethods())
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .ToArray();

        Assert.IsFalse(exposed.Any(type => (type.Namespace ?? String.Empty)
            .StartsWith("EntityForge.Db.Semantic.Durability", StringComparison.Ordinal)),
            "ISemanticDatabase must describe semantic behavior without exposing persistence contracts.");
    }

    [TestMethod]
    public void Consumer_package_member_contracts_match_the_reviewed_baseline()
    {
        UpdateMemberBaselinesWhenRequested();
        string[] expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "PublicMemberApiBaseline.txt"))
            .Where(line => !String.IsNullOrWhiteSpace(line)).Order(StringComparer.Ordinal).ToArray();
        Assembly[] assemblies = ConsumerContractAssemblies();
        string[] actual = assemblies.Select(MemberContractHash).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected, actual, Difference(
            "A public member contract changed. Review parameters, defaults, inheritance and generic constraints intentionally.",
            expected, actual));
    }

    [TestMethod]
    public void Consumer_package_member_contracts_match_the_readable_reviewed_surface()
    {
        UpdateMemberBaselinesWhenRequested();
        string[] expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "PublicMemberApiSurface.txt"));
        string[] actual = ConsumerContractAssemblies().SelectMany(MemberContractLines).ToArray();

        CollectionAssert.AreEqual(expected, actual, Difference(
            "The readable public API surface changed. Review and approve the exact member-level difference.",
            expected, actual));
    }

    private static Assembly[] ConsumerContractAssemblies() =>
    new Assembly[]
    {
        typeof(DatabaseVersion).Assembly, typeof(EntityForge.Db.Mapping.EntityMappingRegistry).Assembly, typeof(ISemanticDatabase).Assembly,
        typeof(GraphEntityStore<>).Assembly, typeof(RdfEntityStore<>).Assembly,
        typeof(SemanticDatabase).Assembly, typeof(IGraphDatabase).Assembly,
        typeof(EntityForge.Db.Graph.Contracts.GraphTransaction).Assembly,
        typeof(RdfDatabase).Assembly, typeof(EntityForge.Db.Rdf.Contracts.RdfTransaction).Assembly,
        typeof(HttpEntityProjectionClient).Assembly, typeof(HttpSemanticEntityClient).Assembly, typeof(EntityForgeHostCapabilitiesTransport).Assembly,
        typeof(GqlProcedureCatalog).Assembly, typeof(EntityForgeDbServer).Assembly,
        typeof(EntityForge.Db.Distributed.Hosting.HttpPartitionEntityClient).Assembly,
        typeof(EntityForge.Db.Distributed.Hosting.PartitionDatabaseNode).Assembly,
        typeof(EntityForge.Db.Distributed.Sharding.LogicalShardId).Assembly,
        typeof(EntityForge.Db.Distributed.Hosting.PartitionApplicationClient).Assembly,
        // These assemblies occur in supported public signatures and are consequently part of the
        // source and binary compatibility boundary even though they predate the database packages.
        typeof(EntityQueryDefinition).Assembly, typeof(SemanticTerm).Assembly,
        typeof(CanonicalJsonValue).Assembly
    }.Distinct().OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal).ToArray();

    private static void UpdateMemberBaselinesWhenRequested()
    {
        if (!String.Equals(Environment.GetEnvironmentVariable("ENTITYFORGE_UPDATE_PUBLIC_API_BASELINES"),
            "1", StringComparison.Ordinal))
        {
            return;
        }

        DirectoryInfo? directory = new(Environment.GetEnvironmentVariable("ENTITYFORGE_PUBLIC_API_BASELINE_ROOT") ?? AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EntityForge.Db.sln")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the EntityForge.Db solution root for baseline update.");

        string baselineDirectory = Path.Combine(directory.FullName, "tests", "Db", "EntityForge.Db.Hosting.Tests");
        Assembly[] assemblies = ConsumerContractAssemblies();
        Assembly[] productAssemblies =
        [
            typeof(SemanticDatabase).Assembly,
            typeof(HttpEntityProjectionClient).Assembly,
            typeof(HttpSemanticEntityClient).Assembly,
            typeof(GraphDatabase).Assembly,
            typeof(IGraphEntityClient).Assembly,
            typeof(GraphEntityStore<>).Assembly,
            typeof(RdfEntityStore<>).Assembly,
            typeof(IRdfEntityClient).Assembly,
            typeof(RdfDatabase).Assembly,
            typeof(EntityForgeHostCapabilitiesTransport).Assembly,
            typeof(GqlProcedureCatalog).Assembly, typeof(EntityForgeDbServer).Assembly
        ];
        File.WriteAllLines(Path.Combine(baselineDirectory, "PublicApiBaseline.txt"),
            productAssemblies.SelectMany(assembly => assembly.GetExportedTypes().Select(type =>
                $"{assembly.GetName().Name}|{type.FullName}")).Order(StringComparer.Ordinal));
        Assembly semanticAssembly = typeof(ISemanticDatabase).Assembly;
        File.WriteAllLines(Path.Combine(baselineDirectory, "SemanticPublicApiBaseline.txt"),
            semanticAssembly.GetExportedTypes().Select(type =>
                $"{semanticAssembly.GetName().Name}|{type.FullName}").Order(StringComparer.Ordinal));
        File.WriteAllLines(Path.Combine(baselineDirectory, "PublicMemberApiBaseline.txt"),
            assemblies.Select(MemberContractHash).Order(StringComparer.Ordinal));
        File.WriteAllLines(Path.Combine(baselineDirectory, "PublicMemberApiSurface.txt"),
            assemblies.SelectMany(MemberContractLines));
    }

    private static string MemberContractHash(Assembly assembly)
    {
        IEnumerable<string> contracts = MemberContractLines(assembly);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(String.Join('\n', contracts))));
        return $"{assembly.GetName().Name}|{hash}";
    }

    private static IEnumerable<string> MemberContractLines(Assembly assembly)
    {
        string assemblyName = assembly.GetName().Name!;
        foreach (string contract in assembly.GetExportedTypes()
            .OrderBy(type => type.FullName, StringComparer.Ordinal).SelectMany(TypeContract))
        {
            yield return $"{assemblyName}|{contract}";
        }
    }

    private static IEnumerable<string> TypeContract(Type type)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;
        yield return $"type|{Name(type)}|base={Name(type.BaseType)}|interfaces={String.Join(',', type.GetInterfaces().Where(item => item.IsVisible).Select(Name).Order())}";
        foreach (Type parameter in type.GetGenericArguments().Where(item => item.IsGenericParameter))
        {
            yield return $"generic|{parameter.Name}|{parameter.GenericParameterAttributes}|{String.Join(',', parameter.GetGenericParameterConstraints().Select(Name).Order())}";
        }

        foreach (ConstructorInfo constructor in type.GetConstructors(declared).OrderBy(item => item.ToString()))
        {
            yield return $"ctor|{Parameters(constructor.GetParameters())}";
        }

        foreach (MethodInfo method in type.GetMethods(declared).Where(item => !item.IsSpecialName).OrderBy(item => item.ToString()))
        {
            yield return $"method|{method.Name}|{Name(method.ReturnType)}|{Parameters(method.GetParameters())}";
        }

        foreach (PropertyInfo property in type.GetProperties(declared).OrderBy(item => item.Name))
        {
            yield return $"property|{property.Name}|{Name(property.PropertyType)}|get={property.GetMethod is not null}|set={property.SetMethod is not null}";
        }

        foreach (FieldInfo field in type.GetFields(declared).OrderBy(item => item.Name))
        {
            yield return $"field|{field.Name}|{Name(field.FieldType)}|{(field.IsLiteral ? field.GetRawConstantValue() : "-")}";
        }

        foreach (EventInfo eventInfo in type.GetEvents(declared).OrderBy(item => item.Name))
        {
            yield return $"event|{eventInfo.Name}|{Name(eventInfo.EventHandlerType)}";
        }
    }

    private static string Parameters(IEnumerable<ParameterInfo> parameters) => String.Join(',', parameters.Select(parameter =>
        $"{parameter.Name}:{Name(parameter.ParameterType)}:optional={parameter.IsOptional}:default={parameter.DefaultValue}"));

    private static string Name(Type? type) => type?.FullName ?? "-";

    private static string Difference(string heading, IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> actual)
    {
        string[] removed = expected.Except(actual, StringComparer.Ordinal).ToArray();
        string[] added = actual.Except(expected, StringComparer.Ordinal).ToArray();
        return $"{heading}{Environment.NewLine}Removed:{Environment.NewLine}{String.Join(Environment.NewLine, removed)}" +
            $"{Environment.NewLine}Added:{Environment.NewLine}{String.Join(Environment.NewLine, added)}";
    }
}
