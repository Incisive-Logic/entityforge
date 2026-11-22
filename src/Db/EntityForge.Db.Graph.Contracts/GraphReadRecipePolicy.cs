using EntityForge.Db.Mapping;

namespace EntityForge.Db.Graph;

internal sealed class GraphReadRecipePolicy : IEntityReadRecipePolicy
{
    internal static readonly GraphReadRecipePolicy Instance = new();

    public DatabaseProfileId Profile => DatabaseProfileId.PropertyGraph;

    public bool SupportsContributionGuard => false;

    public void ValidateShape(EntityReadShape shape)
    {
        if (shape.Kind != "vertex")
        {
            throw new ArgumentException("Unsupported Graph read shape.");
        }
    }
}
