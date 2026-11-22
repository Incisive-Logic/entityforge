using System.Text.Json;
using EntityForge.Db.Query;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Semantics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class SemanticSortFragmentTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Typed_fragment_sort_keys_survive_transport_and_keep_missing_values_last(bool descending)
    {
        var concept = new SemanticTerm(new Uri("urn:item"), SemanticTermRole.Concept);
        var property = new SemanticTerm(new Uri("urn:amount"), SemanticTermRole.Property);
        var scheme = new SemanticIdentityScheme(new Uri("urn:id"), "Identity", SemanticValueKind.String);
        SemanticProjectionFragmentRoot Root(string id, SemanticValue? value) => new(
            new(concept, scheme, new StringSemanticValue(id)), id, [value]);
        SemanticProjectionFragmentRoot[] roots = [Root("ten", new IntegerSemanticValue(10)),
            Root("two-b", new DecimalSemanticValue(2)), Root("missing", null), Root("two-a", new IntegerSemanticValue(2))];
        var transported = JsonSerializer.Deserialize<SemanticProjectionFragmentRoot[]>(JsonSerializer.Serialize(roots))!;
        var query = new EntityQueryDefinition(concept, new EntityProjectionDefinition(concept), orderings:
            [new EntityQueryOrdering(property, descending ? EntityQueryOrderDirection.Descending : EntityQueryOrderDirection.Ascending)]);
        var result = PortableEntityQueryExecutor.OrderFragmentRoots(transported, query).Select(root => root.ProjectedIdentity).ToArray();
        CollectionAssert.AreEqual(descending ? new[] { "ten", "two-a", "two-b", "missing" } : new[] { "two-a", "two-b", "ten", "missing" }, result);
    }
}
