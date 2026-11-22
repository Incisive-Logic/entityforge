using EntityForge.Query.Results;
using EntityForge.Semantics;
using EntityForge.Query.Values;

namespace EntityForge.Query.Projection;

/// <summary>Identifies a recoverable condition encountered while constructing an entity projection.</summary>
public enum EntityProjectionProjectionDiagnosticCode
{
    /// <summary>Matching field candidates exist, but none has evidence from a preferred provider.</summary>
    PreferredProviderNotFound,

    /// <summary>A traversed relationship points to an entity missing from the supplied graph result.</summary>
    RelationshipTargetNotFound
}

/// <summary>Describes a projection condition associated with one entity.</summary>
/// <param name="Code">The diagnostic category.</param>
/// <param name="Message">The human-readable explanation.</param>
/// <param name="EntityIdentity">The entity associated with the diagnostic.</param>
public sealed record EntityProjectionProjectionDiagnostic(
    EntityProjectionProjectionDiagnosticCode Code,
    string Message,
    GraphElementIdentity EntityIdentity);

/// <summary>A projected entity view with diagnostics and optional provider paging continuation.</summary>
/// <param name="Document">The projected entity view, which may be partial by design.</param>
/// <param name="Diagnostics">Conditions encountered while constructing the view.</param>
/// <param name="NextContinuation">An opaque continuation supplied by a paging provider, or null when absent.</param>
public sealed record EntityProjectionProjectionResult(
    EntityProjection Document,
    IReadOnlyList<EntityProjectionProjectionDiagnostic> Diagnostics,
    string? NextContinuation = null);

/// <summary>Builds bounded entity documents from neutral graph results using a projection definition.</summary>
public sealed class EntityProjectionProjector
{
    /// <summary>Projects matching graph roots, selected fields and bounded relationships into an entity document.</summary>
    /// <param name="source">Graph-result envelope containing nodes, edges and optional evidence.</param>
    /// <param name="definition">Root concept and field/relationship selection.</param>
    /// <param name="options">Expansion limits and evidence policy; null uses defaults.</param>
    /// <returns>The entity document and diagnostics collected during projection.</returns>
    /// <exception cref="ArgumentNullException">The source or definition is null.</exception>
    /// <exception cref="ArgumentException">The source does not contain a graph result.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Relationship depth is negative or the entity-expansion budget is not positive.</exception>
    /// <exception cref="EntityProjectionExpansionLimitException">Traversal exceeds the configured expansion budget.</exception>
    public EntityProjectionProjectionResult Project(
        QueryResultEnvelope source,
        EntityProjectionDefinition definition,
        EntityProjectionProjectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(definition);
        options ??= new EntityProjectionProjectionOptions();
        if (options.MaximumRelationshipDepth < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum relationship depth cannot be negative.");
        }
        if (options.MaximumExpandedEntities <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum expanded entities must be positive.");
        }

        if (source.Result is not GraphResult graph)
        {
            throw new ArgumentException("Entity documents currently require a graph result.", nameof(source));
        }

        var nodes = graph.Nodes.ToDictionary(node => node.Identity);
        var edges = new ProjectionEdges(
            graph.Edges.ToLookup(edge => edge.Source),
            graph.Edges.ToLookup(edge => edge.Target));
        var diagnostics = new List<EntityProjectionProjectionDiagnostic>();
        var traversal = new ProjectionTraversal(options.MaximumExpandedEntities);
        var roots = graph.Nodes
            .Where(node => HasType(node, definition.Concept))
            .Select(node => ProjectNode(
                node,
                definition,
                edges,
                nodes,
                source,
                options,
                depth: 0,
                visited: new HashSet<GraphElementIdentity>(),
                traversal,
                diagnostics))
            .OfType<EntityProjectionNode>()
            .ToArray();
        var evidence = options.IncludeEvidence ? source.Evidence : Array.Empty<EvidenceRecord>();
        var document = new EntityProjection(source.QueryId, roots, evidence, source.SemanticModels);

        return new EntityProjectionProjectionResult(document, diagnostics.AsReadOnly());
    }

    private static EntityProjectionItem ProjectNode(
        NodeValue node,
        EntityProjectionDefinition definition,
        ProjectionEdges edges,
        IReadOnlyDictionary<GraphElementIdentity, NodeValue> nodes,
        QueryResultEnvelope envelope,
        EntityProjectionProjectionOptions options,
        int depth,
        HashSet<GraphElementIdentity> visited,
        ProjectionTraversal traversal,
        ICollection<EntityProjectionProjectionDiagnostic> diagnostics)
    {
        if (depth > options.MaximumRelationshipDepth ||
            options.RevisitedEntityPolicy is RevisitedEntityPolicy.Reference && !visited.Add(node.Identity))
        {
            return new EntityProjectionReference(node.Identity);
        }

        if (options.RevisitedEntityPolicy is RevisitedEntityPolicy.DuplicateUntilDepthLimit)
        {
            visited.Add(node.Identity);
        }

        var fields = definition.Fields
            .Select(field => ProjectField(node, field, envelope, options, diagnostics))
            .Where(field => field.Values.Count > 0)
            .ToList();
        if (options.UnmappedPropertyPolicy is UnmappedPropertyPolicy.IncludeBySourceIdentifier)
        {
            AddUnmappedFields(node, definition, fields, options);
        }

        var relationships = definition.Relationships
            .Select(relationship => ProjectRelationship(
                node,
                relationship,
                edges,
                nodes,
                envelope,
                options,
                depth,
                visited,
                traversal,
                diagnostics))
            .ToArray();

        return new EntityProjectionNode(node.Identity, GetProjectedTypes(node), fields, relationships);
    }

    private static EntityProjectionField ProjectField(
        NodeValue node,
        EntityFieldProjection definition,
        QueryResultEnvelope envelope,
        EntityProjectionProjectionOptions options,
        ICollection<EntityProjectionProjectionDiagnostic> diagnostics)
    {
        var candidates = node.Properties
            .Where(assertion => Matches(assertion.SourceTerm, assertion.Alignments, definition.Property))
            .ToArray();
        var selected = definition.ValueSelection switch
        {
            EntityFieldValueSelection.AllCandidates => candidates,
            EntityFieldValueSelection.FirstCandidate => candidates.Take(1).ToArray(),
            EntityFieldValueSelection.PreferredProvider => SelectPreferred(
                node.Identity,
                candidates,
                definition,
                envelope,
                diagnostics),
            _ => throw new NotSupportedException($"Value selection '{definition.ValueSelection}' is not supported.")
        };
        var values = selected.Select(assertion => new EntityProjectionValue(
            assertion.Value,
            assertion.SourceTerm,
            definition.Property,
            options.IncludeEvidence ? assertion.Evidence : Array.Empty<EvidenceReference>()));

        return new EntityProjectionField(definition.Name, values);
    }

    private static PropertyAssertion[] SelectPreferred(
        GraphElementIdentity identity,
        IReadOnlyList<PropertyAssertion> candidates,
        EntityFieldProjection definition,
        QueryResultEnvelope envelope,
        ICollection<EntityProjectionProjectionDiagnostic> diagnostics)
    {
        foreach (var provider in definition.PreferredProviders)
        {
            var candidate = candidates.FirstOrDefault(assertion => assertion.Evidence.Any(reference =>
                envelope.TryGetEvidence(reference, out var evidence) && evidence!.ProviderIdentifier == provider));
            if (candidate is not null)
            {
                return new[] { candidate };
            }
        }

        if (candidates.Count > 0)
        {
            diagnostics.Add(new EntityProjectionProjectionDiagnostic(
                EntityProjectionProjectionDiagnosticCode.PreferredProviderNotFound,
                $"No candidate for field '{definition.Name}' came from a preferred provider.",
                identity));
        }

        return Array.Empty<PropertyAssertion>();
    }

    private static EntityProjectionRelationship ProjectRelationship(
        NodeValue node,
        EntityRelationshipProjection definition,
        ProjectionEdges edges,
        IReadOnlyDictionary<GraphElementIdentity, NodeValue> nodes,
        QueryResultEnvelope envelope,
        EntityProjectionProjectionOptions options,
        int depth,
        HashSet<GraphElementIdentity> visited,
        ProjectionTraversal traversal,
        ICollection<EntityProjectionProjectionDiagnostic> diagnostics)
    {
        IEnumerable<EdgeValue> matchingEdges =
            (definition.Direction is EntityRelationshipDirection.Outgoing
                ? edges.Outgoing[node.Identity]
                : edges.Incoming[node.Identity]).Where(edge =>
                Matches(edge.Relationship.SourceTerm, edge.Relationship.Alignments,
                    definition.Relationship));
        var items = new List<EntityProjectionItem>();
        foreach (var edge in matchingEdges)
        {
            var targetIdentity = definition.Direction is EntityRelationshipDirection.Outgoing
                ? edge.Target
                : edge.Source;
            if (!nodes.TryGetValue(targetIdentity, out var target))
            {
                traversal.RecordExpansion();
                diagnostics.Add(new EntityProjectionProjectionDiagnostic(
                    EntityProjectionProjectionDiagnosticCode.RelationshipTargetNotFound,
                    $"Relationship target '{targetIdentity}' is not present in the graph result.",
                    node.Identity));
                items.Add(new EntityProjectionReference(targetIdentity));
                continue;
            }

            if (!HasType(target, definition.Target.Concept))
            {
                continue;
            }

            traversal.RecordExpansion();
            items.Add(ProjectNode(
                target,
                definition.Target,
                edges,
                nodes,
                envelope,
                options,
                depth + 1,
                visited,
                traversal,
                diagnostics));
        }

        return new EntityProjectionRelationship(definition.Name, items);
    }

    private static void AddUnmappedFields(
        NodeValue node,
        EntityProjectionDefinition definition,
        ICollection<EntityProjectionField> fields,
        EntityProjectionProjectionOptions options)
    {
        var mappedAssertions = definition.Fields
            .SelectMany(field => node.Properties.Where(assertion =>
                Matches(assertion.SourceTerm, assertion.Alignments, field.Property)))
            .ToHashSet();
        foreach (var group in node.Properties
                     .Where(assertion => !mappedAssertions.Contains(assertion))
                     .GroupBy(assertion => assertion.SourceTerm))
        {
            fields.Add(new EntityProjectionField(
                group.Key.Identifier.AbsoluteUri,
                group.Select(assertion => new EntityProjectionValue(
                    assertion.Value,
                    assertion.SourceTerm,
                    assertion.SourceTerm,
                    options.IncludeEvidence ? assertion.Evidence : Array.Empty<EvidenceReference>()))));
        }
    }

    private static bool HasType(NodeValue node, SemanticTerm concept) =>
        node.Types.Any(type => Matches(type.SourceTerm, type.Alignments, concept));

    private static bool Matches(
        SemanticTerm source,
        IEnumerable<SemanticAlignment> alignments,
        SemanticTerm requested) =>
        source == requested || alignments.Any(alignment => alignment.AlignedTerm == requested);

    private static IEnumerable<SemanticTerm> GetProjectedTypes(NodeValue node) =>
        node.Types.SelectMany(type =>
            type.Alignments.Count > 0
                ? type.Alignments.Select(alignment => alignment.AlignedTerm)
                : new[] { type.SourceTerm });

    private sealed class ProjectionTraversal(int maximumExpandedEntities)
    {
        private int _expandedEntities;

        public void RecordExpansion()
        {
            _expandedEntities++;
            if (_expandedEntities > maximumExpandedEntities)
            {
                throw new EntityProjectionExpansionLimitException(maximumExpandedEntities);
            }
        }
    }

    private sealed record ProjectionEdges(
        ILookup<GraphElementIdentity, EdgeValue> Outgoing,
        ILookup<GraphElementIdentity, EdgeValue> Incoming);
}
