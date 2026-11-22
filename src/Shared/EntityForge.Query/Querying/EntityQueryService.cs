using EntityForge.Query.Projection;
using EntityForge.Query.Results;
using EntityForge.Semantics;
using EntityForge.Query.Semantics;

namespace EntityForge.Query.Querying;

/// <summary>
/// Provider-neutral entity-query orchestration. A host supplies the provider selected by its
/// configured data source; the service performs shared semantic alignment and projection.
/// </summary>
public sealed class EntityQueryService : IEntityQueryService
{
    private readonly IEntityQueryProvider provider;
    private readonly IReadOnlyList<SemanticMappingSet> mappingSets;
    private readonly EntityProjectionProjectionOptions projectionOptions;

    /// <summary>Creates query orchestration over one selected provider and a captured set of semantic mappings.</summary>
    /// <param name="provider">The provider responsible for native execution.</param>
    /// <param name="mappingSets">Mapping sets copied into this service for post-execution alignment.</param>
    /// <param name="projectionOptions">Optional relationship-expansion and evidence limits; null uses the projector defaults.</param>
    /// <exception cref="ArgumentNullException">The provider or mapping-set sequence is null.</exception>
    public EntityQueryService(
        IEntityQueryProvider provider,
        IEnumerable<SemanticMappingSet> mappingSets,
        EntityProjectionProjectionOptions? projectionOptions = null)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.mappingSets = Array.AsReadOnly(
            mappingSets?.ToArray() ?? throw new ArgumentNullException(nameof(mappingSets)));
        this.projectionOptions = projectionOptions ?? new EntityProjectionProjectionOptions();
    }

    /// <inheritdoc />
    /// <exception cref="EntityQueryNotSupportedException">The query requests features absent from the provider capabilities.</exception>
    /// <exception cref="ArgumentNullException">The query or execution context is null.</exception>
    public async Task<EntityProjectionPage> ExecuteAsync(
        EntityQueryDefinition query,
        EntityQueryExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        var validation = new EntityQueryCapabilityValidator().Validate(query, provider.Capabilities);
        if (!validation.IsSupported)
        {
            throw new EntityQueryNotSupportedException(
                provider.ProviderKind,
                validation.Issues);
        }

        var native = await provider.ExecuteAsync(query, context, cancellationToken).ConfigureAwait(false);
        var alignment = new SemanticAligner().Align(native.Result.Result, mappingSets);
        var alignedEnvelope = new QueryResultEnvelope(
            native.Result.QueryId,
            alignment.Value,
            native.Result.CorrelationId,
            native.Result.Evidence,
            native.Result.SemanticModels,
            native.Result.Diagnostics.Concat(alignment.Diagnostics.Select(diagnostic =>
                new QueryResultDiagnostic(
                    diagnostic.Code.ToString(),
                    diagnostic.Message,
                    QueryDiagnosticSeverity.Warning))));
        var projection = new EntityProjectionProjector().Project(
            alignedEnvelope,
            query.Projection,
            projectionOptions);
        var page = native.Page ?? new EntityPageResult(
            projection.Document.Roots.Select(root => root.Identity),
            hasNextPage: false,
            endCursor: null);
        return new EntityProjectionPage(projection.Document, page);
    }
}

/// <summary>Reports entity-query features not supported by the selected provider's capability declarations.</summary>
public sealed class EntityQueryNotSupportedException : Exception
{
    /// <summary>Builds a diagnostic message and captures the capability issues.</summary>
    /// <param name="providerKind">The provider identifier.</param>
    /// <param name="issues">The issue sequence, enumerated for the message and again for the captured list; keep it stable.</param>
    public EntityQueryNotSupportedException(
        string providerKind,
        IEnumerable<EntityQueryCapabilityIssue> issues)
        : base($"Provider '{providerKind}' cannot execute this entity query: " +
               string.Join("; ", issues.Select(issue => $"{issue.Path}: {issue.Message}")))
    {
        ProviderKind = providerKind;
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    /// <summary>Gets the provider whose declared capabilities do not cover the query.</summary>
    public string ProviderKind
    {
        get;
    }

    /// <summary>Gets the captured query locations and capability mismatch explanations.</summary>
    public IReadOnlyList<EntityQueryCapabilityIssue> Issues
    {
        get;
    }
}
