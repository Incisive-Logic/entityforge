using EntityForge.Db.Semantic.Durability;
using EntityForge.Db.Semantic.Core;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Semantic.Engine;

internal sealed class DatabaseMutationContext
{
    public DatabaseMutationContext(SemanticCommitPreparationCoordinator coordinator, ISemanticCommitLog log,
        ISemanticDatabaseStateReader state,
        Action<CommittedTransactionDependencies> recordValidation,
        IEntityForgeCommitObserver observer)
    {
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        State = state ?? throw new ArgumentNullException(nameof(state));
        RecordValidation = recordValidation ?? throw new ArgumentNullException(nameof(recordValidation));
        Observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    public SemanticCommitPreparationCoordinator Coordinator
    {
        get;
    }

    public ISemanticCommitLog Log
    {
        get;
    }

    public ISemanticDatabaseStateReader State
    {
        get;
    }

    public Action<CommittedTransactionDependencies> RecordValidation
    {
        get;
    }

    public IEntityForgeCommitObserver Observer
    {
        get;
    }
}
