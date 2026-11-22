using EntityForge.Db.Semantic;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class SemanticEntityQueryInvalidationAnalyzer
{
    private readonly HashSet<Uri> concepts = [];
    private readonly HashSet<Uri> terms = [];

    public SemanticEntityQueryInvalidationAnalyzer(EntityQueryDefinition query, SemanticCatalogSnapshot catalog)
    {
        concepts.Add(query.RootConcept.Identifier);
        if (query.ConceptMatchMode is EntityConceptMatchMode.Entailed)
        {
            var index = new SemanticAxiomIndex(catalog.Vocabularies.Select(vocabulary =>
                new SemanticAxiomSet(new Uri(vocabulary.Identifier, "#published-axioms"), vocabulary.Version,
                    vocabulary.Axioms)));
            foreach (SemanticTerm concept in index.GetTransitiveSubTerms(query.RootConcept))
            {
                concepts.Add(concept.Identifier);
            }
        }
        Projection(query.Projection);
        Filter(query.Filter);
        foreach (EntityQueryOrdering ordering in query.Orderings)
        {
            terms.Add(ordering.Property.Identifier);
        }
    }

    public bool IsRelevant(SemanticCommittedEffect effect) => effect switch
    {
        SemanticFieldAssertionAdded item => EntityTerm(item.Assertion.Subject, item.Assertion.Field),
        SemanticFieldAssertionRetracted item => EntityTerm(item.Assertion.Subject, item.Assertion.Field),
        SemanticRelationshipAssertionAdded item => Relationship(item.Assertion),
        SemanticRelationshipAssertionRetracted item => Relationship(item.Assertion),
        SemanticConceptMembershipChanged item => concepts.Contains(item.Subject.Concept.Identifier),
        SemanticFieldEvidenceChanged item => EntityTerm(item.Subject, item.Term),
        SemanticRelationshipEvidenceChanged item => concepts.Contains(item.Subject.Concept.Identifier) && terms.Contains(item.Relationship.Identifier),
        SemanticGovernanceRulePublished => true,
        _ => false
    };

    private bool EntityTerm(SemanticEntityIdentity entity, SemanticTerm term) =>
        concepts.Contains(entity.Concept.Identifier) && terms.Contains(term.Identifier);

    private bool Relationship(SemanticRelationshipEvidence value) => terms.Contains(value.Relationship.Identifier) &&
        (concepts.Contains(value.Subject.Concept.Identifier) || concepts.Contains(value.Target.Concept.Identifier));

    private void Projection(EntityProjectionDefinition projection)
    {
        concepts.Add(projection.Concept.Identifier);
        foreach (EntityFieldProjection field in projection.Fields)
        {
            terms.Add(field.Property.Identifier);
        }

        foreach (EntityRelationshipProjection relationship in projection.Relationships)
        {
            terms.Add(relationship.Relationship.Identifier);
            Projection(relationship.Target);
        }
    }

    private void Filter(EntityQueryFilter? filter)
    {
        switch (filter)
        {
            case PropertyComparisonEntityQueryFilter item:
                terms.Add(item.Property.Identifier);
                break;
            case PropertyExistsEntityQueryFilter item:
                terms.Add(item.Property.Identifier);
                break;
            case RelationshipExistsEntityQueryFilter item:
                terms.Add(item.Relationship.Identifier);
                concepts.Add(item.TargetConcept.Identifier);
                Filter(item.TargetFilter);
                break;
            case AndEntityQueryFilter item:
                foreach (EntityQueryFilter operand in item.Operands)
                {
                    Filter(operand);
                }

                break;
            case OrEntityQueryFilter item:
                foreach (EntityQueryFilter operand in item.Operands)
                {
                    Filter(operand);
                }

                break;
            case NotEntityQueryFilter item:
                Filter(item.Operand);
                break;
        }
    }
}
