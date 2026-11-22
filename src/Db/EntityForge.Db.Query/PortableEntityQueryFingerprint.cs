using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EntityForge.Query.Projection;
using EntityForge.Query.Querying;
using EntityForge.Query.Values;

namespace EntityForge.Db.Query;

internal static class PortableEntityQueryFingerprint
{
    public static string Create(EntityQueryDefinition query, EntityProjectionProjectionOptions options)
    {
        var value = new StringBuilder();
        Add(value, query.RootConcept.Identifier.AbsoluteUri);
        Add(value, query.ConceptMatchMode.ToString());
        Projection(value, query.Projection);
        Filter(value, query.Filter);
        foreach (EntityQueryOrdering ordering in query.Orderings)
        {
            Add(value, ordering.Property.Identifier.AbsoluteUri);
            Add(value, ordering.Direction);
            Add(value, ordering.ValueSelection);
            Add(value, ordering.MissingValuePlacement);
        }
        Add(value, query.Page?.First ?? 0);
        foreach (IdentifierValue identity in query.Identities?.Identifiers
                     .OrderBy(item => item.Identifier, StringComparer.Ordinal) ?? Enumerable.Empty<IdentifierValue>())
        {
            Add(value, identity.Identifier);
        }

        foreach (TemporalEntityQueryScope scope in query.Scopes.OfType<TemporalEntityQueryScope>()
                     .OrderBy(item => item.Dimension))
        {
            Add(value, scope.Dimension);
            Add(value, scope.FromInclusive?.ToString("O", CultureInfo.InvariantCulture));
            Add(value, scope.ToExclusive?.ToString("O", CultureInfo.InvariantCulture));
        }
        if (query.AssertionPolicy is { } policy)
        {
            foreach (Uri provider in policy.AllowedProviders.OrderBy(item => item.AbsoluteUri, StringComparer.Ordinal))
            {
                Add(value, provider.AbsoluteUri);
            }

            foreach (Uri provider in policy.ExcludedProviders.OrderBy(item => item.AbsoluteUri, StringComparer.Ordinal))
            {
                Add(value, provider.AbsoluteUri);
            }

            foreach (Uri vocabulary in policy.AllowedSourceVocabularies
                         .OrderBy(item => item.AbsoluteUri, StringComparer.Ordinal))
            {
                Add(value, vocabulary.AbsoluteUri);
            }

            foreach (Uri vocabulary in policy.AllowedTargetVocabularies
                         .OrderBy(item => item.AbsoluteUri, StringComparer.Ordinal))
            {
                Add(value, vocabulary.AbsoluteUri);
            }

            Add(value, policy.RequireValidityInterval);
            Add(value, policy.RequireResponsibleAgent);
        }
        Add(value, options.MaximumRelationshipDepth);
        Add(value, options.MaximumExpandedEntities);
        Add(value, options.RevisitedEntityPolicy);
        Add(value, options.UnmappedPropertyPolicy);
        Add(value, options.IncludeEvidence);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private static void Projection(StringBuilder value, EntityProjectionDefinition projection)
    {
        Add(value, projection.Concept.Identifier.AbsoluteUri);
        foreach (EntityFieldProjection field in projection.Fields)
        {
            Add(value, field.Property.Identifier.AbsoluteUri);
            Add(value, field.Name);
            Add(value, field.ValueSelection);
            foreach (Uri provider in field.PreferredProviders)
            {
                Add(value, provider.AbsoluteUri);
            }
        }
        foreach (EntityRelationshipProjection relationship in projection.Relationships)
        {
            Add(value, relationship.Relationship.Identifier.AbsoluteUri);
            Add(value, relationship.Name);
            Add(value, relationship.Direction);
            Projection(value, relationship.Target);
        }
    }

    private static void Filter(StringBuilder value, EntityQueryFilter filter)
    {
        Add(value, filter.GetType().Name);
        switch (filter)
        {
            case PropertyComparisonEntityQueryFilter comparison:
                Add(value, comparison.Property.Identifier.AbsoluteUri);
                Add(value, comparison.Operator);
                Literal(value, (LiteralValue)comparison.Value);
                break;
            case PropertyExistsEntityQueryFilter exists:
                Add(value, exists.Property.Identifier.AbsoluteUri);
                break;
            case RelationshipExistsEntityQueryFilter relationship:
                Add(value, relationship.Relationship.Identifier.AbsoluteUri);
                Add(value, relationship.Direction);
                Add(value, relationship.TargetConcept.Identifier.AbsoluteUri);
                Filter(value, relationship.TargetFilter);
                break;
            case AndEntityQueryFilter and:
                foreach (EntityQueryFilter operand in and.Operands)
                {
                    Filter(value, operand);
                }

                break;
            case OrEntityQueryFilter or:
                foreach (EntityQueryFilter operand in or.Operands)
                {
                    Filter(value, operand);
                }

                break;
            case NotEntityQueryFilter not:
                Filter(value, not.Operand);
                break;
        }
    }

    private static void Literal(StringBuilder value, LiteralValue literal)
    {
        Add(value, literal.LexicalValue);
        Add(value, literal.ScalarKind);
        Add(value, literal.DatatypeIdentifier?.AbsoluteUri);
        Add(value, literal.Language);
        Add(value, literal.Direction);
    }

    private static void Add(StringBuilder target, object? value)
    {
        string text = value switch
        {
            null => String.Empty,
            IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture) ?? String.Empty,
            _ => value.ToString() ?? String.Empty
        };
        target.Append(text.Length).Append(':').Append(text).Append(';');
    }
}
