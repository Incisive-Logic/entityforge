using EntityForge.Db.AppModel.Queries;
using EntityForge.Db;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class EntityQueryBlockTests
{
    [TestMethod]
    public void Semantic_query_keeps_catalogue_concepts_explicit()
    {
        var property = new SemanticQueryProperty("https://example.test/name", "Name", QueryValueEditor.Text,
            new HashSet<QueryComparisonOperator> { QueryComparisonOperator.Equals });
        var query = new SemanticQueryDocument(
            new SemanticQueryConcept("https://example.test/Company", "Company"), [],
            [new SemanticQueryProjectionField(property, "name")]);

        Assert.AreEqual("Company", query.PrimaryConcept.DisplayName);
        Assert.AreEqual("name", query.Projection.Single().ResultName);
    }

    [TestMethod]
    public void Identifier_fallback_produces_a_readable_name()
    {
        Assert.AreEqual("Registered Company", QueryDisplayName.FromIdentifier(
            "https://example.test/vocabulary/registeredCompany"));
    }
}
