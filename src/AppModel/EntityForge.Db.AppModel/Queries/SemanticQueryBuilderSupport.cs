using System.Globalization;
using System.Text.Json;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>Bounds the simple builder to query shapes it can edit without losing meaning.</summary>
public static class SemanticQueryBuilderSupport
{
    /// <summary>Checks whether the simple builder can represent this document using the supplied catalog without losing meaning.</summary>
    /// <param name="metadata">Catalog definitions used for concept, property and relationship checks.</param>
    /// <param name="query">Query document to inspect.</param>
    /// <returns>True for a supported editable shape; false for unsupported values, metadata, nesting, duplicate projection properties or options.</returns>
    /// <remarks>Checks page sizes from 1 through 10,000 and bounds nested groups/relationships. This does not contact a server, authorize execution or prove that the query fits its runtime budget.</remarks>
    public static bool CanEdit(SemanticQueryMetadata metadata, SemanticQueryDocument query)
    {
        return query.First is >= 1 and <= 10_000 &&
            Enum.IsDefined(query.ConceptMatchMode) &&
            SupportsConcept(query.PrimaryConcept.Identifier) &&
            query.Projection.Select(field => field.Property.Identifier).Distinct(StringComparer.Ordinal).Count() == query.Projection.Count &&
            query.Projection.All(field => Properties(query.PrimaryConcept.Identifier).Any(property =>
                property.Identifier == field.Property.Identifier)) &&
            (query.OrderBy ?? []).All(order => Properties(query.PrimaryConcept.Identifier).Any(p => p.Identifier == order.Property.Identifier) &&
                Enum.IsDefined(order.Direction) && Enum.IsDefined(order.ValueSelection) && Enum.IsDefined(order.MissingValues)) &&
            SupportsBlocks(query.PrimaryConcept.Identifier, query.Conditions, 0);

        bool SupportsConcept(string concept) => metadata.Concepts.Any(item => item.Identifier == concept);
        IReadOnlyList<SemanticQueryProperty> Properties(string concept) =>
            metadata.PropertiesByConcept.TryGetValue(concept, out var properties) ? properties : [];
        bool SupportsBlocks(string concept, IReadOnlyList<SemanticQueryBlock> blocks, int depth)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case SemanticPropertyConditionBlock condition:
                        var property = Properties(concept).FirstOrDefault(item => item.Identifier == condition.Property.Identifier);
                        if (property is null || property.Editor != condition.Property.Editor ||
                            condition.Property.LiteralKind is { } kind && property.LiteralKind is not null && kind != property.LiteralKind || !property.Operators.Contains(condition.Operator) ||
                            !SupportsValue(property, condition.Operator, condition.Value) ||
                            condition.Operator == QueryComparisonOperator.In && condition.Value.EnumerateArray().Any(value =>
                                value.ValueKind == JsonValueKind.String && (value.GetString()!.Length == 0 || value.GetString()!.Contains('\n') || value.GetString()!.Contains('\r'))))
                        {
                            return false;
                        }

                        break;
                    case SemanticRelationshipConditionBlock relationship:
                        if (depth >= 4 || !Enum.IsDefined(relationship.Operator) ||
                            !SupportsConcept(relationship.TargetConcept.Identifier) ||
                            !metadata.RelationshipsByConcept.TryGetValue(concept, out var relationships))
                        {
                            return false;
                        }

                        var definition = relationships.FirstOrDefault(item => item.Identifier == relationship.Relationship.Identifier);
                        if (definition is null || definition.TargetTypes is { Count: > 0 } targets &&
                            !targets.Contains(relationship.TargetConcept.Identifier, StringComparer.Ordinal) ||
                            !SupportsBlocks(relationship.TargetConcept.Identifier, relationship.Conditions, depth + 1))
                        {
                            return false;
                        }

                        break;
                    case SemanticLogicalQueryBlock logical:
                        if (depth >= 4 || !Enum.IsDefined(logical.Operator) || !SupportsBlocks(concept, logical.Children, depth + 1))
                        {
                            return false;
                        }

                        break;
                    default:
                        return false;
                }
            }
            return true;
        }
    }

    /// <summary>Checks an operand's JSON shape and scalar representation for the selected editor category.</summary>
    /// <param name="property">Value category and optional literal-kind refinement.</param>
    /// <param name="op">Comparison determining whether an operand or nonempty array is needed.</param>
    /// <param name="value">JSON operand; presence and missing tests do not inspect it.</param>
    /// <returns>Whether the operand can be represented by this editor.</returns>
    /// <remarks>This does not check the property's advertised operator set or catalog membership. Use <see cref="CanEdit"/> for document-level checks; successful value validation alone does not authorize an operator.</remarks>
    public static bool SupportsValue(SemanticQueryProperty property, QueryComparisonOperator op, JsonElement value)
    {
        if (op is QueryComparisonOperator.IsPresent or QueryComparisonOperator.IsMissing)
        {
            return true;
        }

        if (op == QueryComparisonOperator.In)
        {
            return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 &&
                value.EnumerateArray().All(item => Scalar(property, item));
        }

        return Scalar(property, value);
    }

    private static bool Scalar(SemanticQueryProperty property, JsonElement value)
    {
        if (property.Editor == QueryValueEditor.Boolean)
        {
            return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }

        if (property.Editor == QueryValueEditor.Number)
        {
            return value.ValueKind == JsonValueKind.Number &&
                (property.LiteralKind == "integer" ? value.TryGetInt64(out _) : value.TryGetDecimal(out _));
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string text = value.GetString()!;
        return property.Editor switch
        {
            QueryValueEditor.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            QueryValueEditor.DateTime => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _) &&
                (text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || text.Length >= 6 && (text[^6] == '+' || text[^6] == '-')),
            QueryValueEditor.Identifier when property.LiteralKind == "guid" => Guid.TryParse(text, out _),
            QueryValueEditor.Identifier when property.LiteralKind == "absoluteUri" => Uri.TryCreate(text, UriKind.Absolute, out _),
            _ => true
        };
    }
}
