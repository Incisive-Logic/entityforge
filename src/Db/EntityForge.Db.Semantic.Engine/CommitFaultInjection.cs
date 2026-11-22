namespace EntityForge.Db.Semantic.Engine;

internal enum EntityForgeCommitStage
{
    BeforeDurableCommit,
    AfterDurableCommit,
    AfterPublication
}

internal interface IEntityForgeCommitObserver
{
    void Reached(EntityForgeCommitStage stage);
}

internal sealed class NoEntityForgeCommitObserver : IEntityForgeCommitObserver
{
    public void Reached(EntityForgeCommitStage stage)
    {
    }
}

internal sealed class EntityForgeInjectedCommitFailureException(EntityForgeCommitStage stage)
    : EntityForge.Db.EntityForgeDbException($"An injected EntityForgeDB commit failure occurred at '{stage}'.")
{
    public EntityForgeCommitStage Stage { get; } = stage;
}
