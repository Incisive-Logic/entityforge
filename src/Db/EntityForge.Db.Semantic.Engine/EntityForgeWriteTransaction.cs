using EntityForge.Db.Semantic;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Routing;
using EntityForge.Query.Projection;
using EntityForge.Semantics;

namespace EntityForge.Db.Semantic.Engine;

/// <summary>
/// Represents an optimistic semantic write transaction with snapshot-consistent reads and explicit commit.
/// </summary>
/// <remarks>
/// Operations on one transaction must be awaited sequentially. Overlapping queries, governance evaluations,
/// promotion or commit are rejected so every recorded read dependency is included in conflict validation.
/// </remarks>
public sealed class EntityForgeWriteTransaction
{
    private readonly SemanticDatabaseEngine _database;
    private readonly List<SemanticPredicateDependency> _predicateReads = new();
    private readonly object _operationGate = new();
    private TransactionOperationState _operationState;

    internal EntityForgeWriteTransaction(SemanticDatabaseEngine database, SemanticSessionEnvelope session,
        IReadOnlyList<SemanticEntityChange> changes, IReadOnlyList<SemanticWriteDependency> writes,
        IReadOnlyList<SemanticPredicateDependency> predicateWrites, DatabaseVersion readVersion,
        PropertyStorageCatalogState propertyStorage, GovernanceCatalogState governance,
        RoutedParticipantSet participants, Guid? resumableTransactionId = null,
        IEnumerable<SemanticPredicateDependency>? predicateReads = null,
        bool isInitialBulkLoad = false)
    {
        _database = database;
        Session = session;
        Changes = changes;
        OwnershipWrites = writes;
        PredicateWrites = predicateWrites;
        ReadVersion = readVersion;
        PropertyStorage = propertyStorage;
        Governance = governance;
        Participants = participants;
        ResumableTransactionId = resumableTransactionId;
        IsInitialBulkLoad = isInitialBulkLoad;
        _predicateReads.AddRange(predicateReads ?? Array.Empty<SemanticPredicateDependency>());
    }

    /// <summary>Gets the semantic database version against which the transaction validates.</summary>
    public DatabaseVersion ReadVersion
    {
        get;
    }

    /// <summary>Gets the routed shard participants captured for the transaction.</summary>
    public RoutedParticipantSet Participants
    {
        get;
    }

    internal SemanticSessionEnvelope Session
    {
        get;
    }

    internal IReadOnlyList<SemanticEntityChange> Changes
    {
        get;
    }

    internal IReadOnlyList<SemanticWriteDependency> OwnershipWrites
    {
        get;
    }

    internal IReadOnlyList<SemanticPredicateDependency> PredicateReads
    {
        get
        {
            lock (_operationGate)
            {
                return _predicateReads.ToArray();
            }
        }
    }

    internal IReadOnlyList<SemanticPredicateDependency> PredicateWrites
    {
        get;
    }

    internal PropertyStorageCatalogState PropertyStorage
    {
        get;
    }

    internal GovernanceCatalogState Governance
    {
        get;
    }

    internal Guid? ResumableTransactionId
    {
        get;
    }

    internal bool IsInitialBulkLoad
    {
        get;
    }

    /// <summary>Executes an equality query and records its predicate dependency.</summary>
    public ValueTask<SemanticFieldQueryResult> QueryAsync(SemanticFieldEqualityQuery query,
        CancellationToken cancellationToken = default) => _database.QueryAsync(this, query, cancellationToken);

    /// <summary>Executes a string range query and records its predicate dependency.</summary>
    public ValueTask<SemanticFieldQueryResult> QueryRangeAsync(SemanticStringRangeQuery query,
        CancellationToken cancellationToken = default) => _database.QueryRangeAsync(this, query, cancellationToken);

    /// <summary>Queries concept membership and records its predicate dependency.</summary>
    public ValueTask<IReadOnlyList<SemanticEntityMembershipEvidence>> QueryConceptAsync(
        SemanticConceptMembershipQuery query, CancellationToken cancellationToken = default) =>
        _database.QueryConceptAsync(this, query, cancellationToken);

    /// <summary>Evaluates one published governance rule against the transaction's prospective state.</summary>
    public ValueTask<SemanticGovernanceDecision> EvaluateGovernanceAsync(Uri ruleIdentifier,
        CancellationToken cancellationToken = default) =>
        _database.EvaluateGovernanceAsync(this, ruleIdentifier, cancellationToken);

    internal void AcceptPredicateReads(IEnumerable<SemanticPredicateDependency> dependencies)
    {
        lock (_operationGate)
        {
            foreach (SemanticPredicateDependency dependency in dependencies)
            {
                if (!_predicateReads.Contains(dependency))
                {
                    _predicateReads.Add(dependency);
                }
            }
        }
    }

    internal IDisposable EnterOperation()
    {
        lock (_operationGate)
        {
            if (_operationState is TransactionOperationState.Completed)
            {
                throw new InvalidOperationException("A completed write transaction cannot execute operations.");
            }

            if (_operationState is TransactionOperationState.Active)
            {
                throw new InvalidOperationException(
                    "A write transaction supports only one in-flight operation at a time.");
            }

            _operationState = TransactionOperationState.Active;
            return new OperationLease(this);
        }
    }

    /// <summary>Validates and durably commits this transaction.</summary>
    public ValueTask<SessionCommitResult> CommitAsync(CancellationToken cancellationToken = default)
    {
        Complete();
        return _database.CommitAsync(this, cancellationToken);
    }

    internal ValueTask<SessionCommitResult> CommitAuthoritativeAsync(
        DatabaseVersion authoritativeVersion, CancellationToken cancellationToken = default)
    {
        Complete();
        return _database.ApplyAuthoritativeCommitAsync(this, authoritativeVersion, cancellationToken);
    }

    internal void DetachForResume(SemanticDatabaseEngine database)
    {
        if (!ReferenceEquals(_database, database))
        {
            throw new InvalidOperationException("A transaction can only be promoted by its owning database.");
        }

        Complete();
    }

    private void Complete()
    {
        lock (_operationGate)
        {
            if (_operationState is TransactionOperationState.Active)
            {
                throw new InvalidOperationException(
                    "A write transaction cannot complete while another operation is in flight.");
            }

            if (_operationState is TransactionOperationState.Completed)
            {
                throw new InvalidOperationException("A write transaction can complete only once.");
            }

            _operationState = TransactionOperationState.Completed;
        }
    }

    private void ExitOperation()
    {
        lock (_operationGate)
        {
            if (_operationState is not TransactionOperationState.Active)
            {
                throw new InvalidOperationException("The write transaction operation is not active.");
            }

            _operationState = TransactionOperationState.Open;
        }
    }

    private enum TransactionOperationState
    {
        Open, Active, Completed
    }

    private sealed class OperationLease(EntityForgeWriteTransaction transaction) : IDisposable
    {
        private EntityForgeWriteTransaction? _transaction = transaction;

        public void Dispose() => Interlocked.Exchange(ref _transaction, null)?.ExitOperation();
    }
}
