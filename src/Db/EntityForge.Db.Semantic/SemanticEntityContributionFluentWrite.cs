using System.Collections;
using EntityForge.Db.Mapping;
using EntityForge.Semantics.Protobuf;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic;

/// <summary>
/// Identifies the source-owned record whose contribution is being changed, together with the
/// provenance and producer identity required to make that change auditable.
/// </summary>
/// <param name="Source">Source session position owning the contribution.</param>
/// <param name="Provenance">Evidence attached to the contributed assertions.</param>
/// <param name="ProducerActivityId">Non-empty identity of the producing activity.</param>
/// <param name="SessionId">Optional session identity; a new identity is allocated when omitted.</param>
/// <param name="ChangeId">Optional change identity; a new identity is allocated when omitted.</param>
/// <param name="CorrelationId">Optional application correlation identifier.</param>
/// <param name="Timestamp">Optional change timestamp; the writer uses its current time when omitted.</param>
/// <param name="ProgressPrecondition">Optional expected source progress checked when applying the contribution.</param>
public sealed record SemanticSourceRecord(
    SemanticChangeSessionSource Source,
    SemanticProvenance Provenance,
    Guid ProducerActivityId,
    Guid? SessionId = null,
    Guid? ChangeId = null,
    string? CorrelationId = null,
    DateTimeOffset? Timestamp = null,
    SemanticSourceProgressPrecondition? ProgressPrecondition = null)
{
    /// <summary>Gets the durable source-session position owning the contribution.</summary>
    public SemanticChangeSessionSource Source { get; } = Source ?? throw new ArgumentNullException(nameof(Source));

    /// <summary>Gets the evidence associated with this source record.</summary>
    public SemanticProvenance Provenance { get; } = Provenance ?? throw new ArgumentNullException(nameof(Provenance));

    /// <summary>Gets the identity of the producer applying the contribution.</summary>
    public Guid ProducerActivityId
    {
        get;
    }
    = ProducerActivityId != Guid.Empty
        ? ProducerActivityId
        : throw new ArgumentException("A producer activity identity is required.", nameof(ProducerActivityId));
}

/// <summary>Starts an explicitly source-scoped semantic entity contribution.</summary>
public static class SemanticEntityContributionFluentWriteExtensions
{
    /// <summary>Selects an entity before identifying the source which owns its contribution.</summary>
    public static SemanticEntitySourceSelection ForEntity(
        this ISemanticChangeWriter database,
        SemanticEntityIdentity entity)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(entity);
        return new(database, entity);
    }
}

/// <summary>Selects the source record which owns the contribution to an entity.</summary>
public sealed class SemanticEntitySourceSelection
{
    private readonly ISemanticChangeWriter _database;
    private readonly SemanticEntityIdentity _entity;

    internal SemanticEntitySourceSelection(ISemanticChangeWriter database, SemanticEntityIdentity entity)
    {
        _database = database;
        _entity = entity;
    }

    /// <summary>Selects the source record which owns the following entity mutations.</summary>
    public SemanticSourceEntityContribution From(SemanticSourceRecord sourceRecord)
    {
        ArgumentNullException.ThrowIfNull(sourceRecord);
        return new(_database, _entity, sourceRecord);
    }
}

/// <summary>Changes, replaces, or retracts one source record's contribution to an entity.</summary>
public sealed class SemanticSourceEntityContribution
{
    private readonly ISemanticChangeWriter _database;
    private readonly SemanticEntityIdentity _entity;
    private readonly SemanticSourceRecord _sourceRecord;

    internal SemanticSourceEntityContribution(
        ISemanticChangeWriter database,
        SemanticEntityIdentity entity,
        SemanticSourceRecord sourceRecord)
    {
        _database = database;
        _entity = entity;
        _sourceRecord = sourceRecord;
    }

    /// <summary>Applies only the explicitly described contribution mutations.</summary>
    public ValueTask<SessionCommitResult> ChangeAsync(
        Action<SemanticEntityContributionBuilder> configure,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(SemanticEntityChangeKind.Upsert, configure, cancellationToken);

    /// <summary>Replaces this source record's complete contribution with the described state.</summary>
    public ValueTask<SessionCommitResult> ReplaceAsync(
        Action<SemanticEntityContributionBuilder> configure,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(SemanticEntityChangeKind.ReplaceSourceState, configure, cancellationToken);

    /// <summary>Replaces this source record's contribution using a reusable POCO semantic map.</summary>
    public ValueTask<SessionCommitResult> ReplaceAsync<T>(
        T entity,
        SemanticEntityMap<T> map,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(map);
        if (map.GetIdentity(entity) != _entity)
        {
            throw new ArgumentException("The mapped entity identity does not match ForEntity.", nameof(entity));
        }

        return ApplyAsync(SemanticEntityChangeKind.ReplaceSourceState,
            builder => ConfigureMappedContribution(builder, entity, map), cancellationToken);
    }

    /// <summary>Retracts this source record's complete contribution to the entity.</summary>
    public ValueTask<SessionCommitResult> RetractAsync(CancellationToken cancellationToken = default) =>
        ApplyAsync(SemanticEntityChangeKind.RetractSourceEntity, null, cancellationToken);

    private ValueTask<SessionCommitResult> ApplyAsync(
        SemanticEntityChangeKind kind,
        Action<SemanticEntityContributionBuilder>? configure,
        CancellationToken cancellationToken)
    {
        var builder = new SemanticEntityContributionBuilder();
        configure?.Invoke(builder);
        Guid sessionId = _sourceRecord.SessionId ?? Guid.NewGuid();
        DateTimeOffset timestamp = _sourceRecord.Timestamp ?? DateTimeOffset.UtcNow;
        var change = new SemanticEntityChange(
            _sourceRecord.ChangeId ?? Guid.NewGuid(),
            kind,
            _entity,
            _sourceRecord.Provenance,
            builder.FieldChanges,
            builder.RelationshipChanges);
        var page = new SemanticChangeSessionPage(sessionId, 0, [change]);
        var codec = new SemanticChangeSessionTransportCodec();
        var start = new SemanticChangeSessionStart(
            sessionId,
            SemanticChangeSessionKind.Incremental,
            _sourceRecord.Source,
            _sourceRecord.ProducerActivityId,
            timestamp,
            _sourceRecord.CorrelationId,
            _sourceRecord.ProgressPrecondition);
        var hashes = codec.ComputeHashes(start, [page]);
        var envelope = new SemanticSessionEnvelope(
            start,
            [page],
            new SemanticChangeSessionClose(
                sessionId,
                1,
                1,
                hashes.ContentHash,
                timestamp,
                hashes.OperationHash));
        return _database.ApplyAsync(envelope, cancellationToken);
    }

    internal static void ConfigureMappedContribution<T>(SemanticEntityContributionBuilder builder,
        T entity, SemanticEntityMap<T> map)
    {
        foreach (SemanticMemberBinding binding in map.MemberBindings)
        {
            EntityMemberMap member = map.EntityMap.Members.Single(item => item.Name == binding.MemberName);
            object? raw = member.Read(entity!);
            if (raw is null)
            {
                continue;
            }

            IEnumerable values = raw is string or byte[]? new object[] { raw } :
                raw as IEnumerable ?? new object[] { raw };
            foreach (object value in values)
            {
                if (binding.Role is EntityMemberRole.Property)
                {
                    builder.Set(binding.Term, SemanticMappedValue.Convert(value));
                }
                else if (binding.Role is EntityMemberRole.Relationship)
                {
                    builder.Link(binding.Term, value as SemanticEntityIdentity ??
                        binding.TargetIdentity?.Invoke(value) ?? throw new InvalidOperationException(
                            $"Semantic relationship '{binding.MemberName}' has no target identity mapping."));
                }
            }
        }
    }
}

/// <summary>Describes explicit property and relationship mutations within a source contribution.</summary>
public sealed class SemanticEntityContributionBuilder
{
    private readonly List<SemanticFieldChange> _fieldChanges = [];
    private readonly List<SemanticRelationshipChange> _relationshipChanges = [];

    internal IReadOnlyList<SemanticFieldChange> FieldChanges => _fieldChanges;

    internal IReadOnlyList<SemanticRelationshipChange> RelationshipChanges => _relationshipChanges;

    /// <summary>Replaces the source-owned values of a property with one value.</summary>
    public SemanticEntityContributionBuilder Set(SemanticTerm property, SemanticValue value)
    {
        _fieldChanges.Add(new(property, SemanticFieldChangeKind.Set, value));
        return this;
    }

    /// <summary>Replaces the source-owned values of a property with the supplied values.</summary>
    public SemanticEntityContributionBuilder Set(SemanticTerm property, params SemanticValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("Set requires at least one value; use Clear to remove the property.", nameof(values));
        }

        foreach (SemanticValue value in values)
        {
            Set(property, value);
        }

        return this;
    }

    /// <summary>Adds one value to a source-owned multi-valued property.</summary>
    public SemanticEntityContributionBuilder Add(SemanticTerm property, SemanticValue value)
    {
        _fieldChanges.Add(new(property, SemanticFieldChangeKind.Add, value));
        return this;
    }

    /// <summary>Removes one value from a source-owned multi-valued property.</summary>
    public SemanticEntityContributionBuilder Remove(SemanticTerm property, SemanticValue value)
    {
        _fieldChanges.Add(new(property, SemanticFieldChangeKind.RemoveValue, value));
        return this;
    }

    /// <summary>Removes all source-owned values of a property.</summary>
    public SemanticEntityContributionBuilder Clear(SemanticTerm property)
    {
        _fieldChanges.Add(new(property, SemanticFieldChangeKind.Remove));
        return this;
    }

    /// <summary>
    /// Records that the source explicitly supplied an unknown/null state. This differs from omitting
    /// the property in ChangeAsync and from retracting it with Clear.
    /// </summary>
    public SemanticEntityContributionBuilder SetUnknown(SemanticTerm property)
    {
        _fieldChanges.Add(new(property, SemanticFieldChangeKind.Set, new NullSemanticValue()));
        return this;
    }

    /// <summary>Adds a source-owned relationship to a target entity.</summary>
    public SemanticEntityContributionBuilder Link(
        SemanticTerm relationship,
        SemanticEntityIdentity target)
    {
        _relationshipChanges.Add(new(relationship, SemanticRelationshipChangeKind.Add, target));
        return this;
    }

    /// <summary>Removes a source-owned relationship to a target entity.</summary>
    public SemanticEntityContributionBuilder Unlink(
        SemanticTerm relationship,
        SemanticEntityIdentity target)
    {
        _relationshipChanges.Add(new(relationship, SemanticRelationshipChangeKind.Remove, target));
        return this;
    }

    /// <summary>Removes every source-owned target of a relationship.</summary>
    public SemanticEntityContributionBuilder ClearLinks(SemanticTerm relationship)
    {
        _relationshipChanges.Add(new(relationship, SemanticRelationshipChangeKind.Clear));
        return this;
    }
}
