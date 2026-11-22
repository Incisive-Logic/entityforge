using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Query;
using EntityForge.Db.Telemetry;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class TransactionalSemanticQueryService
{
    private const int SupportedLocalEvaluatorCount = 1;
    private const string PropertyStorageKindTagName = "entityforge.db.property.storage_kind";
    private const string PropertyCatalogVersionTagName = "entityforge.db.property.catalog_version";
    private readonly SemanticCommitPreparationCoordinator _commitGate;
    private readonly ISemanticCommitLog _log;
    private readonly ISemanticDatabaseStateReader _state;
    private readonly EntityForgeDbTelemetry _telemetry;
    private readonly IReadOnlyList<ISemanticGovernanceEvaluator> _governanceEvaluators;
    private readonly SemanticConceptQueryExecutor _conceptQueries = new();

    public TransactionalSemanticQueryService(
        SemanticCommitPreparationCoordinator commitGate,
        ISemanticCommitLog log,
        ISemanticDatabaseStateReader state,
        EntityForgeDbTelemetry telemetry,
        IReadOnlyList<ISemanticGovernanceEvaluator> governanceEvaluators)
    {
        _commitGate = commitGate ?? throw new ArgumentNullException(nameof(commitGate));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _governanceEvaluators = governanceEvaluators ??
            throw new ArgumentNullException(nameof(governanceEvaluators));
    }

    public async ValueTask<SemanticFieldQueryResult> QueryAsync(
        EntityForgeWriteTransaction transaction,
        SemanticFieldEqualityQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable transactionOperation = transaction.EnterOperation();
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.transaction.query");
        bool gateEntered = false;
        try
        {
            await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            SemanticFieldQueryResult result = await _state.ReadView.FieldQueries.ExecuteAsync(
                _state.Current, transaction.PropertyStorage, query, transaction.ReadVersion, cancellationToken)
                .ConfigureAwait(false);
            operation.SetTag(PropertyStorageKindTagName, result.StorageKind.ToString());
            operation.SetTag(PropertyCatalogVersionTagName, result.CatalogVersion.Value);
            await RecordPredicateReadsAsync(transaction, new[]
                {
                    SemanticPredicateDependency.FieldEquals(query.Field, query.Value),
                    SemanticPredicateDependency.CatalogBinding(query.Field)
                }.Concat(GovernanceDependencies(query.Governance)), true, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The transactional semantic field query failed.");
            throw;
        }
        finally
        {
            if (gateEntered)
            {
                _commitGate.Release();
            }
        }
    }

    public async ValueTask<SemanticFieldQueryResult> QueryRangeAsync(
        EntityForgeWriteTransaction transaction,
        SemanticStringRangeQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable transactionOperation = transaction.EnterOperation();
        using EntityForgeDbOperation operation = _telemetry.StartOperation("db.transaction.query.string_range");
        bool gateEntered = false;
        try
        {
            await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            SemanticFieldQueryResult result = await _state.ReadView.FieldQueries.ExecuteAsync(
                _state.Current, transaction.PropertyStorage, query, transaction.ReadVersion, cancellationToken)
                .ConfigureAwait(false);
            await RecordPredicateReadsAsync(transaction, new[]
                {
                    SemanticPredicateDependency.FieldStringRange(
                        query.Field, query.LowerInclusive, query.UpperInclusive),
                    SemanticPredicateDependency.CatalogBinding(query.Field)
                }.Concat(GovernanceDependencies(query.Governance)), true, cancellationToken).ConfigureAwait(false);
            operation.SetTag(PropertyStorageKindTagName, result.StorageKind.ToString());
            operation.SetTag(PropertyCatalogVersionTagName, result.CatalogVersion.Value);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The transactional semantic string range query failed.");
            throw;
        }
        finally
        {
            if (gateEntered)
            {
                _commitGate.Release();
            }
        }
    }

    public async ValueTask<IReadOnlyList<SemanticEntityMembershipEvidence>> QueryConceptAsync(
        EntityForgeWriteTransaction transaction,
        SemanticConceptMembershipQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        using IDisposable transactionOperation = transaction.EnterOperation();
        using EntityForgeDbOperation operation = _telemetry.StartOperation(
            "db.transaction.query.concept_membership");
        bool gateEntered = false;
        try
        {
            await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            DatabaseReadView view = _state.ReadView;
            IReadOnlyList<SemanticEntityMembershipEvidence> result = await _conceptQueries.ExecuteAsync(
                view.State, view.EntityAccessPaths, query, transaction.ReadVersion, cancellationToken)
                .ConfigureAwait(false);
            await RecordPredicateReadsAsync(transaction,
                new[] { SemanticPredicateDependency.Concept(query.Concept) }
                    .Concat(GovernanceDependencies(query.Governance)), true, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            ThrowQuery(exception, operation, "The transactional semantic membership query failed.");
            throw;
        }
        finally
        {
            if (gateEntered)
            {
                _commitGate.Release();
            }
        }
    }

    public async ValueTask<SemanticGovernanceDecision> EvaluateGovernanceAsync(
        EntityForgeWriteTransaction transaction,
        Uri ruleIdentifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ruleIdentifier);
        using IDisposable transactionOperation = transaction.EnterOperation();
        using EntityForgeDbOperation operation = _telemetry.StartOperation(
            "db.transaction.governance.evaluate");
        try
        {
            if (!transaction.Governance.Rules.TryGetValue(ruleIdentifier, out SemanticGovernanceRule? rule))
            {
                throw new EntityForgeGovernanceEvaluationException(
                    $"Governance rule '{ruleIdentifier}' is not present in the transaction catalog snapshot.");
            }

            return await EvaluateGovernanceCoreAsync(transaction, rule, false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.SetOutcome("cancelled");
            throw;
        }
        catch (Exception exception)
        {
            operation.RecordException(exception);
            if (exception is EntityForgeGovernanceEvaluationException)
            {
                throw;
            }

            throw new EntityForgeGovernanceEvaluationException(
                $"Governance rule '{ruleIdentifier}' evaluation failed.", exception);
        }
    }

    public async ValueTask EnforceApplicableGovernanceAsync(
        EntityForgeWriteTransaction transaction,
        CancellationToken cancellationToken)
    {
        SemanticTerm[] concepts = transaction.Changes.Select(change => change.Subject.Concept)
            .Distinct().ToArray();
        SemanticGovernanceRule[] applicable = transaction.Governance.Rules.Values
            .Where(rule => rule.Enabled && rule.Scope is not null &&
                rule.Scope.ApplicableConcepts.Any(concepts.Contains))
            .OrderBy(rule => rule.Identifier.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();
        foreach (SemanticGovernanceRule rule in applicable)
        {
            SemanticGovernanceDecision decision = await EvaluateGovernanceCoreAsync(
                transaction, rule, true, cancellationToken).ConfigureAwait(false);
            if (rule.Scope!.Enforcement is SemanticGovernanceEnforcement.RejectViolation &&
                decision.Kind is SemanticGovernanceDecisionKind.Violated)
            {
                throw new EntityForgeGovernanceViolationException(decision);
            }
        }
    }

    private async ValueTask<SemanticGovernanceDecision> EvaluateGovernanceCoreAsync(
        EntityForgeWriteTransaction transaction,
        SemanticGovernanceRule rule,
        bool commitGateHeld,
        CancellationToken cancellationToken)
    {
        await RecordPredicateReadsAsync(transaction,
            new[] { GovernanceDependency(rule.Kind, rule.Identifier) }, commitGateHeld,
            cancellationToken).ConfigureAwait(false);
        if (!rule.Enabled)
        {
            return new(rule.Identifier, SemanticGovernanceDecisionKind.NotApplicable,
                Array.Empty<SemanticGovernanceIssue>());
        }

        return await EvaluateSessionGovernanceAsync(rule, transaction.Session, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask EnforceDistributedGovernanceAsync(EntityForgeWriteTransaction retained,
        SemanticSessionEnvelope original, IReadOnlyCollection<SemanticTerm> affectedConcepts, CancellationToken token)
    {
        // The caller reserves the exact retained native position on every owner.
        // Evaluate the complete frozen application session, never an owner's fragment.
        foreach (var rule in retained.Governance.Rules.Values.Where(rule => rule.Enabled && rule.Scope is not null &&
            rule.Scope.ApplicableConcepts.Any(affectedConcepts.Contains)).OrderBy(rule => rule.Identifier.AbsoluteUri, StringComparer.Ordinal))
        {
            var decision = await EvaluateSessionGovernanceAsync(rule, original, token).ConfigureAwait(false);
            if (rule.Scope!.Enforcement == SemanticGovernanceEnforcement.RejectViolation &&
                decision.Kind == SemanticGovernanceDecisionKind.Violated)
            {
                throw new EntityForgeGovernanceViolationException(decision);
            }
        }
    }

    private async ValueTask<SemanticGovernanceDecision> EvaluateSessionGovernanceAsync(
        SemanticGovernanceRule rule, SemanticSessionEnvelope session, CancellationToken cancellationToken)
    {
        ISemanticGovernanceEvaluator[] matches = _governanceEvaluators.Where(item => item.Supports(rule)).ToArray();
        if (matches.Length != SupportedLocalEvaluatorCount)
        {
            throw new EntityForgeGovernanceEvaluationException(matches.Length == 0
                ? $"No evaluator supports governance rule '{rule.Identifier}'."
                : $"Multiple evaluators support governance rule '{rule.Identifier}'.");
        }

        SemanticGovernanceDecision decision = await matches[0]
            .EvaluateAsync(rule, session, cancellationToken).ConfigureAwait(false);
        if (decision.RuleIdentifier != rule.Identifier)
        {
            throw new EntityForgeGovernanceEvaluationException(
                "A governance evaluator returned a decision for a different rule.");
        }

        _telemetry.RecordGovernanceDecision(rule.Kind.ToString(), decision.Kind.ToString());
        return decision;
    }

    private async ValueTask RecordPredicateReadsAsync(
        EntityForgeWriteTransaction transaction,
        IEnumerable<SemanticPredicateDependency> dependencies,
        bool commitGateHeld,
        CancellationToken cancellationToken)
    {
        SemanticPredicateDependency[] additions = dependencies.Distinct()
            .Where(item => !transaction.PredicateReads.Contains(item)).ToArray();
        if (additions.Length == 0)
        {
            return;
        }

        if (transaction.ResumableTransactionId is null)
        {
            transaction.AcceptPredicateReads(additions);
            return;
        }
        bool gateEntered = false;
        try
        {
            if (!commitGateHeld)
            {
                await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateEntered = true;
            }
            SemanticDatabaseState state = _state.Current;
            Guid transactionId = transaction.ResumableTransactionId.Value;
            if (!state.ResumableWrites.TryGetValue(transactionId, out ResumableWriteTransaction? persisted))
            {
                throw new InvalidDataException("The resumable transaction intent is not active.");
            }

            SemanticPredicateDescriptor[] reads = persisted.PredicateReads
                .Concat(additions.Select(item => item.ToDescriptor())).Distinct().ToArray();
            ResumableWriteTransaction updated = persisted with
            {
                PredicateReads = reads
            };
            var change = new ResumableWriteChange(ResumableWriteChangeKind.UpdatePredicateReads, updated);
            _ = await _log.CommitAsync(
                DurableDatabaseCommit.ForResumableWrite(change), cancellationToken).ConfigureAwait(false);
            transaction.AcceptPredicateReads(additions);
            _telemetry.RecordResumableWriteChange(change.Kind.ToString());
        }
        finally
        {
            if (gateEntered)
            {
                _commitGate.Release();
            }
        }
    }

    private static IEnumerable<SemanticPredicateDependency> GovernanceDependencies(
        SemanticQueryGovernance? governance)
    {
        if (governance is null)
        {
            yield break;
        }

        ArgumentNullException.ThrowIfNull(governance.ConstraintIdentifiers);
        ArgumentNullException.ThrowIfNull(governance.PolicyIdentifiers);
        foreach (Uri identifier in governance.ConstraintIdentifiers)
        {
            yield return SemanticPredicateDependency.Constraint(identifier);
        }

        foreach (Uri identifier in governance.PolicyIdentifiers)
        {
            yield return SemanticPredicateDependency.Policy(identifier);
        }
    }

    private static SemanticPredicateDependency GovernanceDependency(
        SemanticGovernanceRuleKind kind, Uri identifier) =>
        kind is SemanticGovernanceRuleKind.Constraint
            ? SemanticPredicateDependency.Constraint(identifier)
            : SemanticPredicateDependency.Policy(identifier);

    private static void ThrowQuery(Exception exception, EntityForgeDbOperation operation, string message)
    {
        operation.RecordException(exception);
        if (exception is EntityForgeDbException)
        {
            return;
        }

        throw new EntityForgeDbQueryException(message, exception);
    }
}
