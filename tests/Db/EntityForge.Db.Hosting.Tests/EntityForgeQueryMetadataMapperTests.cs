using EntityForge.Db.Hosting;
using EntityForge.Db.Semantic;
using EntityForge.Db.Transport.Contracts;
using EntityForge.Semantics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityForge.Db.Hosting.Tests;

[TestClass]
public sealed class EntityForgeQueryMetadataMapperTests
{
    [TestMethod]
    public void Catalogue_preserves_resolved_logical_shapes()
    {
        var company = new SemanticTerm(new Uri("https://example.test/Company"), SemanticTermRole.Concept);
        var employee = new SemanticTerm(new Uri("https://example.test/Employee"), SemanticTermRole.Concept);
        var name = new SemanticTerm(new Uri("https://example.test/name"), SemanticTermRole.Property);
        var employs = new SemanticTerm(new Uri("https://example.test/employs"), SemanticTermRole.Relationship);
        var nameDefinition = new SemanticTermDefinition(name, "Registered name", "The legal name.",
            field: new SemanticFieldShape(SemanticValueKind.String, new SemanticCardinality(1, 1)));
        var employsDefinition = new SemanticTermDefinition(employs, "Employs",
            relationship: new SemanticRelationshipShape([company], [employee], new SemanticCardinality(0, null)));
        var vocabulary = new SemanticVocabulary(new Uri("https://example.test/vocabulary"), "2",
        [
            new(company, "Company"), new(employee, "Employee"), nameDefinition, employsDefinition,
        ]);
        var overview = new SemanticResolvedCatalog(
            new SemanticCatalogSnapshot([], [company, employee, name, employs], [], new(3), [vocabulary]),
            [new SemanticResolvedConcept(new SemanticTermDefinition(company, "Company"),
                [new SemanticResolvedProperty(nameDefinition)], [employsDefinition], [], [], [])]);

        SemanticCatalogTransport response = EntityForgeQueryMetadataMapper.Catalog(overview);

        Assert.AreEqual(3, response.VocabularyVersion);
        Assert.AreEqual("Registered name", response.Concepts.Single().Properties.Single().DisplayName);
        Assert.AreEqual("string", response.Concepts.Single().Properties.Single().ValueKind);
        Assert.AreEqual(1, response.Concepts.Single().Properties.Single().Minimum);
        Assert.AreEqual(employee.Identifier.AbsoluteUri,
            response.Concepts.Single().Relationships.Single().TargetConcepts.Single());
        Assert.AreEqual(1, response.Vocabularies.Single().PropertyCount);
    }
}
