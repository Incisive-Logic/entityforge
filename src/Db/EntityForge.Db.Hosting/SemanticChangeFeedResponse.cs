using System.Text.Json;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;

namespace EntityForge.Db.Hosting;

/// <summary>Contains a bounded page of committed semantic changes.</summary>
/// <param name="Changes">Committed transactions in feed order.</param>
/// <param name="NextCursor">Opaque continuation for a subsequent request, when supplied.</param>
/// <param name="HasMore">Whether more changes were available after this page.</param>
public sealed record SemanticChangePageResponse(
    IReadOnlyList<SemanticChangeResponse> Changes,
    string? NextCursor,
    bool HasMore);

/// <summary>Describes one committed semantic transaction.</summary>
/// <param name="DatabaseIncarnation">Database identity that scopes the change cursor and version.</param>
/// <param name="Version">Committed semantic version.</param>
/// <param name="TransactionId">Identity of the committed transaction.</param>
/// <param name="Cursor">Opaque feed position for this transaction.</param>
/// <param name="Effects">Logical effects committed atomically by the transaction.</param>
public sealed record SemanticChangeResponse(
    Guid DatabaseIncarnation,
    long Version,
    Guid TransactionId,
    string Cursor,
    IReadOnlyList<SemanticChangeEffectResponse> Effects);

/// <summary>Describes one transport-neutral semantic effect.</summary>
/// <param name="Kind">Discriminator selecting the effect shape.</param>
/// <param name="Subject">Encoded semantic subject identity, when applicable.</param>
/// <param name="Term">Property, relationship or concept URI, when applicable.</param>
/// <param name="Value">JSON assertion value or structured effect payload, depending on Kind.</param>
/// <param name="Target">Encoded relationship target identity, when applicable.</param>
/// <param name="SourceSystem">Source-system URI associated with assertion evidence.</param>
/// <param name="SourceRecord">Source record identifier associated with assertion evidence.</param>
/// <param name="ObservedAt">Evidence observation time, when applicable.</param>
/// <param name="WasMember">Previous concept membership for membership-change effects.</param>
/// <param name="IsMember">Resulting concept membership for membership-change effects.</param>
/// <param name="BeforeCount">Evidence count before a count-change effect.</param>
/// <param name="AfterCount">Evidence count after a count-change effect.</param>
/// <param name="ValueKind">Semantic value-kind discriminator when a typed value is supplied.</param>
/// <param name="Datatype">Datatype URI for a typed literal.</param>
/// <param name="Language">Language tag for a language-tagged string.</param>
public sealed record SemanticChangeEffectResponse(
    string Kind,
    string? Subject = null,
    string? Term = null,
    JsonElement? Value = null,
    string? Target = null,
    string? SourceSystem = null,
    string? SourceRecord = null,
    DateTimeOffset? ObservedAt = null,
    bool? WasMember = null,
    bool? IsMember = null,
    int? BeforeCount = null,
    int? AfterCount = null,
    string? ValueKind = null,
    string? Datatype = null,
    string? Language = null);

internal static class SemanticChangeFeedResponseMapper
{
    public static SemanticChangePageResponse Map(SemanticCommittedChangePage page) => new(
        page.Changes.Select(change => new SemanticChangeResponse(change.DatabaseIncarnation,
            change.Version.Value, change.TransactionId, change.Cursor.Value,
            change.Effects.Select(Effect).ToArray())).ToArray(),
        page.NextCursor?.Value, page.HasMore);

    private static SemanticChangeEffectResponse Effect(SemanticCommittedEffect effect) => effect switch
    {
        SemanticFieldAssertionAdded value => Assertion("fieldAssertionAdded", value.Assertion),
        SemanticFieldAssertionRetracted value => Assertion("fieldAssertionRetracted", value.Assertion),
        SemanticRelationshipAssertionAdded value => Relationship("relationshipAssertionAdded", value.Assertion),
        SemanticRelationshipAssertionRetracted value =>
            Relationship("relationshipAssertionRetracted", value.Assertion),
        SemanticConceptMembershipChanged value => new("conceptMembershipChanged", Identity(value.Subject),
            WasMember: value.WasMember, IsMember: value.IsMember),
        SemanticFieldEvidenceChanged value => new("fieldEvidenceChanged", Identity(value.Subject),
            value.Term.Identifier.AbsoluteUri, BeforeCount: value.BeforeCount, AfterCount: value.AfterCount),
        SemanticRelationshipEvidenceChanged value => new("relationshipEvidenceChanged", Identity(value.Subject),
            value.Relationship.Identifier.AbsoluteUri, BeforeCount: value.BeforeCount, AfterCount: value.AfterCount),
        SemanticCatalogModelRegistered value => new("catalogModelRegistered",
            Term: value.Model.Identifier.AbsoluteUri),
        SemanticCatalogTermRegistered value => new("catalogTermRegistered",
            Term: value.Term.Identifier.AbsoluteUri),
        SemanticCatalogIdentitySchemeRegistered value => new("catalogIdentitySchemeRegistered",
            Term: value.Scheme.Identifier.AbsoluteUri),
        SemanticGovernanceRulePublished value => new("governanceRulePublished",
            Term: value.After.Identifier.AbsoluteUri),
        SemanticConformanceIssueRecorded value => ConformanceIssue(value),
        _ => throw new NotSupportedException($"Unsupported semantic effect '{effect.GetType().Name}'.")
    };

    private static SemanticChangeEffectResponse ConformanceIssue(SemanticConformanceIssueRecorded value) =>
        new("conformanceIssueRecorded",
            Subject: value.Issue.Subject is { } subject ? Identity(subject) : null,
            Term: value.Issue.Term?.Identifier.AbsoluteUri,
            Value: JsonSerializer.SerializeToElement(new
            {
                sessionId = value.SessionId,
                path = value.Issue.Path,
                code = value.Issue.Code,
                message = value.Issue.Message,
                severity = value.Issue.Severity.ToString(),
                constraint = value.Issue.Constraint?.AbsoluteUri,
                rejectedValue = value.Issue.RejectedValue is { } rejected ? TransportValue(rejected) : (JsonElement?)null
            }),
            ValueKind: value.Issue.RejectedValue?.Kind.ToString());

    private static SemanticChangeEffectResponse Assertion(string kind, SemanticFieldEvidence assertion) =>
        new(kind, Identity(assertion.Subject), assertion.Field.Identifier.AbsoluteUri,
            TransportValue(assertion.Value),
            SourceSystem: assertion.Provenance.SourceSystem.AbsoluteUri,
            SourceRecord: assertion.Provenance.SourceRecord,
            ObservedAt: assertion.Provenance.ObservedAt,
            ValueKind: assertion.Value.Kind.ToString(),
            Datatype: (assertion.Value as TypedLiteralSemanticValue)?.Datatype.AbsoluteUri,
            Language: (assertion.Value as LanguageStringSemanticValue)?.Language);

    private static SemanticChangeEffectResponse Relationship(string kind,
        SemanticRelationshipEvidence assertion) => new(kind, Identity(assertion.Subject),
        assertion.Relationship.Identifier.AbsoluteUri, Target: Identity(assertion.Target),
        SourceSystem: assertion.Provenance.SourceSystem.AbsoluteUri,
        SourceRecord: assertion.Provenance.SourceRecord,
        ObservedAt: assertion.Provenance.ObservedAt);

    private static string Identity(SemanticEntityIdentity identity) => string.Join('|',
        identity.Concept.Identifier.AbsoluteUri, identity.Scheme.Identifier.AbsoluteUri,
        IdentityValue(identity.Value));

    private static JsonElement TransportValue(SemanticValue value) => value switch
    {
        NullSemanticValue => JsonSerializer.SerializeToElement<object?>(null),
        BooleanSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        IntegerSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        FloatingPointSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        DecimalSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        StringSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        LanguageStringSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        DateSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        TimeSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        DateTimeSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        DurationSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        BinarySemanticValue item => JsonSerializer.SerializeToElement(Convert.ToBase64String(item.Value.Span)),
        AbsoluteUriSemanticValue item => JsonSerializer.SerializeToElement(item.Value.AbsoluteUri),
        GuidSemanticValue item => JsonSerializer.SerializeToElement(item.Value),
        JsonSemanticValue item => JsonSerializer.Deserialize<JsonElement>(item.Utf8Json),
        TypedLiteralSemanticValue item => JsonSerializer.SerializeToElement(item.LexicalValue),
        _ => throw new NotSupportedException($"Unsupported semantic value '{value.GetType().Name}'.")
    };

    private static string IdentityValue(SemanticValue value) => value switch
    {
        StringSemanticValue item => item.Value,
        IntegerSemanticValue item => item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        AbsoluteUriSemanticValue item => item.Value.AbsoluteUri,
        GuidSemanticValue item => item.Value.ToString("D"),
        TypedLiteralSemanticValue item => item.LexicalValue,
        _ => TransportValue(value).ToString()
    };
}
