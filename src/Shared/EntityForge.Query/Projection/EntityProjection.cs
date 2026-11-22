using EntityForge.Query.Results;
using EntityForge.Semantics;
using EntityForge.Query.Values;

namespace EntityForge.Query.Projection;

/// <summary>A query-derived view of selected entities, with optional evidence and semantic model references.</summary>
/// <remarks>Its roots may be partial projections. This is not a complete replacement value for entity persistence.</remarks>
public sealed class EntityProjection
{
    /// <summary>Captures projected roots and their associated metadata without cloning node objects.</summary>
    /// <param name="sourceQueryId">The identity of the query that produced this view.</param>
    /// <param name="roots">The nonnull root sequence, copied in input order.</param>
    /// <param name="evidence">Optional evidence records, copied in input order.</param>
    /// <param name="semanticModels">Optional semantic model references, copied and deduplicated.</param>
    /// <exception cref="ArgumentNullException">The root sequence is null.</exception>
    public EntityProjection(
        Guid sourceQueryId,
        IEnumerable<EntityProjectionNode> roots,
        IEnumerable<EvidenceRecord>? evidence = null,
        IEnumerable<SemanticModelReference>? semanticModels = null)
    {
        SourceQueryId = sourceQueryId;
        Roots = Array.AsReadOnly(roots?.ToArray() ?? throw new ArgumentNullException(nameof(roots)));
        Evidence = Array.AsReadOnly((evidence ?? Array.Empty<EvidenceRecord>()).ToArray());
        SemanticModels = Array.AsReadOnly(
            (semanticModels ?? Array.Empty<SemanticModelReference>()).Distinct().ToArray());
    }

    /// <summary>Gets the identity of the originating query.</summary>
    public Guid SourceQueryId
    {
        get;
    }

    /// <summary>Gets the captured root nodes in input order.</summary>
    public IReadOnlyList<EntityProjectionNode> Roots
    {
        get;
    }

    /// <summary>Gets the captured evidence metadata; it may include evidence for more than the selected roots.</summary>
    public IReadOnlyList<EvidenceRecord> Evidence
    {
        get;
    }

    /// <summary>Gets the captured distinct semantic model references.</summary>
    public IReadOnlyList<SemanticModelReference> SemanticModels
    {
        get;
    }
}

/// <summary>A projection restricted to the root identities and order of one entity result page.</summary>
public sealed record EntityProjectionPage
{
    /// <summary>Selects a document's roots using the page identities, retaining its evidence and model metadata.</summary>
    /// <param name="document">The projection with unique root identities from which to select.</param>
    /// <param name="page">The page whose ordered root identities must be present in the projection.</param>
    /// <exception cref="ArgumentException">An argument is null, document roots repeat an identity or a page root is absent.</exception>
    public EntityProjectionPage(EntityProjection document, EntityPageResult page)
    {
        ArgumentNullException.ThrowIfNull(document);
        Page = page ?? throw new ArgumentNullException(nameof(page));
        var rootsByIdentity = document.Roots.ToDictionary(root => root.Identity);
        var selectedRoots = page.RootIdentities.Select(identity =>
            rootsByIdentity.TryGetValue(identity, out var root)
                ? root
                : throw new ArgumentException(
                    $"Page root '{identity}' is not present in the entity projection.", nameof(page)));
        Document = new EntityProjection(
            document.SourceQueryId,
            selectedRoots,
            document.Evidence,
            document.SemanticModels);
    }

    /// <summary>Gets the new projection containing only the selected roots in page order.</summary>
    public EntityProjection Document
    {
        get;
    }

    /// <summary>Gets the paging metadata supplied at construction.</summary>
    public EntityPageResult Page
    {
        get;
    }
}

/// <summary>Base representation for an expanded projected node or an identity-only reference.</summary>
public abstract record EntityProjectionItem;

/// <summary>A reference to an entity whose properties and relationships are not expanded here.</summary>
/// <param name="Identity">The referenced entity identity.</param>
public sealed record EntityProjectionReference(GraphElementIdentity Identity) : EntityProjectionItem;

/// <summary>A projected value retaining both its source term and the term selected by the projection.</summary>
public sealed record EntityProjectionValue
{
    /// <summary>Captures a value's source and projected vocabulary terms with distinct evidence references.</summary>
    /// <param name="value">The nonnull query value; retained without cloning.</param>
    /// <param name="sourceTerm">The nonnull term under which the value was asserted.</param>
    /// <param name="projectedTerm">The nonnull term under which the projection presents it.</param>
    /// <param name="evidence">Optional evidence references; copied and deduplicated.</param>
    /// <exception cref="ArgumentNullException">A value or term is null.</exception>
    public EntityProjectionValue(
        QueryValue value,
        SemanticTerm sourceTerm,
        SemanticTerm projectedTerm,
        IEnumerable<EvidenceReference>? evidence = null)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        SourceTerm = sourceTerm ?? throw new ArgumentNullException(nameof(sourceTerm));
        ProjectedTerm = projectedTerm ?? throw new ArgumentNullException(nameof(projectedTerm));
        Evidence = Array.AsReadOnly((evidence ?? Array.Empty<EvidenceReference>()).Distinct().ToArray());
    }

    /// <summary>Gets the projected query value.</summary>
    public QueryValue Value
    {
        get;
    }

    /// <summary>Gets the original asserted vocabulary term.</summary>
    public SemanticTerm SourceTerm
    {
        get;
    }

    /// <summary>Gets the vocabulary term used to present the value in this projection.</summary>
    public SemanticTerm ProjectedTerm
    {
        get;
    }

    /// <summary>Gets the captured distinct evidence references.</summary>
    public IReadOnlyList<EvidenceReference> Evidence
    {
        get;
    }
}

/// <summary>A named projected field containing zero or more values with source and evidence metadata.</summary>
public sealed record EntityProjectionField
{
    /// <summary>Captures a field's values in input order without deduplicating them.</summary>
    /// <param name="name">The nonblank output field name.</param>
    /// <param name="values">The nonnull value sequence to copy.</param>
    /// <exception cref="ArgumentException">The name is blank or an argument is null.</exception>
    public EntityProjectionField(string name, IEnumerable<EntityProjectionValue> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Values = Array.AsReadOnly(values?.ToArray() ?? throw new ArgumentNullException(nameof(values)));
    }

    /// <summary>Gets the output field name.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Gets the captured values in input order.</summary>
    public IReadOnlyList<EntityProjectionValue> Values
    {
        get;
    }
}

/// <summary>A named projected relationship containing expanded nodes or identity-only references.</summary>
public sealed record EntityProjectionRelationship
{
    /// <summary>Captures the related items in input order without deduplicating them.</summary>
    /// <param name="name">The nonblank output relationship name.</param>
    /// <param name="items">The nonnull sequence of projected nodes or references.</param>
    /// <exception cref="ArgumentException">The name is blank or an argument is null.</exception>
    public EntityProjectionRelationship(string name, IEnumerable<EntityProjectionItem> items)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        Items = Array.AsReadOnly(items?.ToArray() ?? throw new ArgumentNullException(nameof(items)));
    }

    /// <summary>Gets the output relationship name.</summary>
    public string Name
    {
        get;
    }

    /// <summary>Gets the captured expanded nodes and references in input order.</summary>
    public IReadOnlyList<EntityProjectionItem> Items
    {
        get;
    }
}

/// <summary>An entity view containing selected types, fields and relationships rather than a complete persistence value.</summary>
public sealed record EntityProjectionNode : EntityProjectionItem
{
    /// <summary>Captures one projected entity, deduplicating types while preserving field and relationship order.</summary>
    /// <param name="identity">The nonnull entity identity.</param>
    /// <param name="types">The nonnull type sequence; copied and deduplicated.</param>
    /// <param name="fields">The nonnull selected field sequence; copied.</param>
    /// <param name="relationships">The nonnull selected relationship sequence; copied.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public EntityProjectionNode(
        GraphElementIdentity identity,
        IEnumerable<SemanticTerm> types,
        IEnumerable<EntityProjectionField> fields,
        IEnumerable<EntityProjectionRelationship> relationships)
    {
        Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        Types = SnapshotDistinct(types, nameof(types));
        Fields = Snapshot(fields, nameof(fields));
        Relationships = Snapshot(relationships, nameof(relationships));
    }

    /// <summary>Gets the projected entity identity.</summary>
    public GraphElementIdentity Identity
    {
        get;
    }

    /// <summary>Gets the captured distinct type terms.</summary>
    public IReadOnlyList<SemanticTerm> Types
    {
        get;
    }

    /// <summary>Gets the captured selected fields; absent fields are not declarations that stored data is absent.</summary>
    public IReadOnlyList<EntityProjectionField> Fields
    {
        get;
    }

    /// <summary>Gets the captured selected relationships, which may be only a partial view of stored relationships.</summary>
    public IReadOnlyList<EntityProjectionRelationship> Relationships
    {
        get;
    }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T>? source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (source.TryGetNonEnumeratedCount(out int count) && count == 0)
        {
            return Array.Empty<T>();
        }

        T[] values = source.ToArray();
        return values.Length == 0 ? Array.Empty<T>() : Array.AsReadOnly(values);
    }

    private static IReadOnlyList<T> SnapshotDistinct<T>(IEnumerable<T>? source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        if (source.TryGetNonEnumeratedCount(out int count))
        {
            if (count == 0)
            {
                return Array.Empty<T>();
            }

            if (count == 1)
            {
                return Snapshot(source, parameterName);
            }
        }
        T[] values = source.Distinct().ToArray();
        return values.Length == 0 ? Array.Empty<T>() : Array.AsReadOnly(values);
    }
}
