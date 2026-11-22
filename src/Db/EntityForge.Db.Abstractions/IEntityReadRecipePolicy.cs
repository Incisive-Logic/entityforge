namespace EntityForge.Db.Mapping;

internal interface IEntityReadRecipePolicy
{
    DatabaseProfileId Profile
    {
        get;
    }

    bool SupportsContributionGuard
    {
        get;
    }

    void ValidateShape(EntityReadShape shape);
}
