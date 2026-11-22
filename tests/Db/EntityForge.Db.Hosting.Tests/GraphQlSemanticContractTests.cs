using EntityForge.Db.Query.Adapters;
using System.Text.Json;
using EntityForge.Db.Hosting;
using EntityForge.Query.Querying;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class GraphQlSemanticContractTests
{
    [TestMethod]
    public void Graphql_preserves_direct_matching_and_ordered_sort_keys()
    {
        var compiled = new GraphQlEntityQueryCompiler(null).Compile("""
            { entities(concept: "https://example.test/Company", conceptMatchMode: direct,
                orderBy: [
                  {property: "https://example.test/name", direction: ascending},
                  {property: "https://example.test/revenue", direction: descending}
                ]) { nodes { identity } } }
            """, null);
        Assert.AreEqual("direct", compiled.Request.ConceptMatchMode);
        Assert.AreEqual(2, compiled.Request.OrderBy!.Count);
        Assert.AreEqual("https://example.test/name", compiled.Request.OrderBy[0].Property);
        Assert.AreEqual("descending", compiled.Request.OrderBy[1].Direction);
        var neutral = new PortableEntityQueryRequestCompiler().Compile(compiled.Request);
        Assert.AreEqual(EntityConceptMatchMode.Direct, neutral.Query.ConceptMatchMode);
        Assert.AreEqual(2, neutral.Query.Orderings.Count);
    }

    [TestMethod]
    public void Entity_and_aggregate_queries_select_identical_semantic_population()
    {
        const string options = """
            concept: "https://example.test/Company", conceptMatchMode: direct,
            identities: ["https://example.test/company/1"],
            snapshotLeaseId: "7e249bc2-3f21-4b62-9719-535088abc35f",
            where: {property: "https://example.test/name", startsWith: "Acme"},
            validTime: {fromInclusive: "2026-01-01T00:00:00Z", toExclusive: "2026-02-01T00:00:00Z"},
            observationTime: {toExclusive: "2026-03-01T00:00:00Z"},
            assertions: {allowedProviders: ["https://example.test/provider"], requireValidityInterval: true}
            """;
        var entity = new GraphQlEntityQueryCompiler(null).Compile(
            "{ entities(" + options + ") { nodes { identity } } }", null);
        var aggregate = new GraphQlSemanticAggregationCompiler().Compile(
            "{ aggregateEntities(" + options + ", measures: [{function: countEntities, name: \"count\"}]) { inputEntityCount } }", null);
        Assert.AreEqual(JsonSerializer.Serialize(entity.Request with
        {
            Projection = aggregate.Request.Source.Projection
        }),
            JsonSerializer.Serialize(aggregate.Request.Source));
        _ = new PortableEntityQueryRequestCompiler().Compile(aggregate.Request.Source);
    }

    [TestMethod]
    public void Existing_single_order_and_default_entailment_are_preserved()
    {
        var query = new GraphQlEntityQueryCompiler(null).Compile("""
            { entities(concept: "https://example.test/Company",
                orderBy: {property: "https://example.test/name"}) { nodes { identity } } }
            """, null);
        Assert.AreEqual("entailed", query.Request.ConceptMatchMode);
        Assert.AreEqual(1, query.Request.OrderBy!.Count);
    }

    [TestMethod]
    public void Aggregation_rejects_malformed_source_policy_instead_of_ignoring_it()
    {
        Assert.ThrowsException<EntityForgeGraphQlException>(() =>
            new GraphQlSemanticAggregationCompiler().Compile("""
                { aggregateEntities(concept: "https://example.test/Company", assertions: false,
                  measures: [{function: countEntities, name: "count"}]) { inputEntityCount } }
                """, null));
    }

    [TestMethod]
    public void Ordering_list_rejects_non_object_members()
    {
        Assert.ThrowsException<EntityForgeGraphQlException>(() =>
            new GraphQlEntityQueryCompiler(null).Compile("""
                { entities(concept: "https://example.test/Company",
                  orderBy: [{property: "https://example.test/name"}, false]) { nodes { identity } } }
                """, null));
    }

    [TestMethod]
    public void Design_schema_parses_and_all_documented_queries_compile_to_semantic_queries()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "GraphQlDesign");
        _ = GraphQLParser.Parser.Parse(File.ReadAllText(Path.Combine(root, "SEMANTIC_GRAPHQL_SCHEMA.graphql")));
        string design = File.ReadAllText(Path.Combine(root, "SEMANTIC_GRAPHQL_DESIGN.md"));
        var examples = System.Text.RegularExpressions.Regex.Matches(design, "```graphql\\s*([\\s\\S]*?)```");
        Assert.AreEqual(4, examples.Count);
        foreach (System.Text.RegularExpressions.Match example in examples)
        {
            string source = example.Groups[1].Value;
            if (source.TrimStart().StartsWith("mutation", StringComparison.Ordinal))
            {
                _ = GraphQLParser.Parser.Parse(source);
                var variables = System.Text.RegularExpressions.Regex.Match(design, "```json\\s*([\\s\\S]*?)```");
                using var input = System.Text.Json.JsonDocument.Parse(variables.Groups[1].Value);
                Assert.AreEqual(1, SemanticGraphQlMutations.Compile(input.RootElement.GetProperty("input")).Pages[0].Changes.Count);
                continue;
            }
            if (GraphQlSemanticAggregationCompiler.IsAggregation(source, null))
            {
                var aggregate = new GraphQlSemanticAggregationCompiler().Compile(source, null);
                var neutral = new PortableEntityQueryRequestCompiler().Compile(aggregate.Request.Source);
                _ = PortableSemanticAggregationMapper.Compile(aggregate.Request,
                    new CompiledHostedEntityQuery(neutral.Query, neutral.ProjectionOptions, null));
            }
            else
            {
                var query = new GraphQlEntityQueryCompiler(null).Compile(source, null);
                _ = new PortableEntityQueryRequestCompiler().Compile(query.Request);
            }
        }
    }
}
