using System.Text.Json;
using EntityForge.Db.AppModel.Queries;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class SemanticQueryBuilderSupportTests
{
    private static readonly SemanticQueryConcept Company = new("company", "Company");
    private static readonly SemanticQueryProperty Name = new("name", "Name", QueryValueEditor.Text,
        new HashSet<QueryComparisonOperator> { QueryComparisonOperator.Equals });

    private static readonly SemanticQueryRelationship Related = new("related", "Related", ["company"]);
    private static readonly SemanticQueryMetadata Metadata = new([Company],
        new Dictionary<string, IReadOnlyList<SemanticQueryProperty>> { ["company"] = [Name] },
        new Dictionary<string, IReadOnlyList<SemanticQueryRelationship>> { ["company"] = [Related] });

    private static SemanticPropertyConditionBlock Condition => new("name-filter", Name,
        QueryComparisonOperator.Equals, JsonSerializer.SerializeToElement("Acme"));

    private static SemanticQueryDocument Query(params SemanticQueryBlock[] blocks) => new(Company, blocks, [new(Name, "customName")]);

    [TestMethod]
    public void Supports_simple_filters_and_relationship_conjunctions()
    {
        Assert.IsTrue(SemanticQueryBuilderSupport.CanEdit(Metadata, Query(Condition,
            new SemanticRelationshipConditionBlock("related", Related, Company, QueryLogicalOperator.All, [Condition]))));
    }

    [TestMethod]
    [DataRow(QueryLogicalOperator.All)]
    [DataRow(QueryLogicalOperator.Any)]
    [DataRow(QueryLogicalOperator.None)]
    public void Explicit_groups_are_editable_without_flattening(QueryLogicalOperator op)
    {
        Assert.IsTrue(SemanticQueryBuilderSupport.CanEdit(Metadata, Query(new SemanticLogicalQueryBlock("group", op, [Condition]))));
    }

    [TestMethod]
    [DataRow(QueryLogicalOperator.Any)]
    [DataRow(QueryLogicalOperator.None)]
    public void Relationship_target_groups_are_editable(QueryLogicalOperator op)
    {
        Assert.IsTrue(SemanticQueryBuilderSupport.CanEdit(Metadata, Query(
            new SemanticRelationshipConditionBlock("related", Related, Company, op, [Condition]))));
    }

    [TestMethod]
    public void Missing_metadata_and_non_scalar_values_are_not_editable()
    {
        Assert.IsFalse(SemanticQueryBuilderSupport.CanEdit(Metadata, Query(Condition with
        {
            Property = Name with
            {
                Identifier = "missing"
            }
        })));
        Assert.IsFalse(SemanticQueryBuilderSupport.CanEdit(Metadata, Query(Condition with
        {
            Value = JsonSerializer.SerializeToElement(new[] { "a", "b" })
        })));
        Assert.IsFalse(SemanticQueryBuilderSupport.CanEdit(Metadata, Query() with
        {
            First = 10001
        }));
        Assert.IsFalse(SemanticQueryBuilderSupport.CanEdit(Metadata, Query() with
        {
            Projection = [new(Name, "one"), new(Name, "two")]
        }));
    }
}
