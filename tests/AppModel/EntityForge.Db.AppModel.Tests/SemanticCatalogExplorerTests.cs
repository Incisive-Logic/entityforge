using EntityForge.Db.AppModel.Catalogs;
using EntityForge.Db.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.AppModel.Tests;

[TestClass]
public sealed class SemanticCatalogExplorerTests
{
    [TestMethod]
    public void Fragment_identifiers_remain_distinct_in_hierarchy_and_selection()
    {
        SemanticCatalogConcept company = Concept("Company") with
        {
            Identifier = new Uri("https://example.test/model#Company")
        };
        SemanticCatalogConcept person = Concept("Person") with
        {
            Identifier = new Uri("https://example.test/model#Person")
        };
        SemanticCatalogConcept employee = Concept("Employee", [person.Identifier]) with
        {
            Identifier = new Uri("https://example.test/model#Employee"),
        };
        var explorer = new SemanticCatalogExplorer();
        explorer.SetCatalog(new SemanticCatalog(1, [], [], [company, person]));
        Assert.AreEqual(2, explorer.VisibleConcepts.Count);
        Assert.IsFalse(explorer.HasHierarchy);
        explorer.Select(person.Identifier);
        Assert.AreSame(person, explorer.SelectedConcept);
        Assert.ThrowsExactly<ArgumentException>(() => explorer.Select(new Uri("https://example.test/model#Missing")));

        explorer.SetCatalog(new SemanticCatalog(1, [], [], [employee, company, person]));
        Assert.IsTrue(explorer.HasHierarchy);
        CollectionAssert.AreEqual(new[] { "Company", "Person", "Employee" },
            explorer.VisibleConcepts.Select(row => row.Concept.DisplayName).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 0, 1 }, explorer.VisibleConcepts.Select(row => row.Depth).ToArray());
        explorer.Select(employee.Identifier);
        Assert.AreSame(employee, explorer.SelectedConcept);
        explorer.SetView(SemanticCatalogView.Alphabetical);
        Assert.AreEqual(3, explorer.VisibleConcepts.Count);
    }

    [TestMethod]
    public void Hierarchy_orders_narrower_concepts_beneath_their_broader_concept()
    {
        SemanticCatalogConcept person = Concept("Person");
        SemanticCatalogConcept employee = Concept("Employee", [person.Identifier]);
        var explorer = new SemanticCatalogExplorer();

        explorer.SetCatalog(new SemanticCatalog(1, [], [], [employee, person]));

        Assert.AreEqual("Person", explorer.VisibleConcepts[0].Concept.DisplayName);
        Assert.AreEqual(0, explorer.VisibleConcepts[0].Depth);
        Assert.AreEqual("Employee", explorer.VisibleConcepts[1].Concept.DisplayName);
        Assert.AreEqual(1, explorer.VisibleConcepts[1].Depth);
        Assert.IsTrue(explorer.HasHierarchy);
    }

    [TestMethod]
    public void Search_includes_concepts_matching_member_names()
    {
        SemanticCatalogConcept employee = Concept("Employee") with
        {
            Properties = [new(new Uri("https://example.test/startDate"), "Start date",
                EntityForge.Semantics.SemanticValueKind.Date, 0, 1)],
        };
        var explorer = new SemanticCatalogExplorer();
        explorer.SetCatalog(new SemanticCatalog(1, [], [], [Concept("Company"), employee]));

        explorer.SetFilter("start date");

        Assert.AreEqual(1, explorer.VisibleConcepts.Count);
        Assert.AreEqual("Employee", explorer.VisibleConcepts[0].Concept.DisplayName);
    }

    [TestMethod]
    public void Related_concepts_can_become_the_current_selection()
    {
        SemanticCatalogConcept employee = Concept("Employee");
        SemanticCatalogConcept manager = Concept("Manager", [employee.Identifier]);
        var explorer = new SemanticCatalogExplorer();
        explorer.SetCatalog(new SemanticCatalog(1, [], [], [employee, manager]));

        explorer.Select(manager.Identifier);

        Assert.AreEqual("Manager", explorer.SelectedConcept!.DisplayName);
    }

    private static SemanticCatalogConcept Concept(string name, IReadOnlyList<Uri>? broader = null) => new(
        new Uri($"https://example.test/{name}"), name, null, [], [], broader ?? [], [], []);
}
