using EntityForge.Db.Mapping;

namespace EntityForge.Db.Semantic;

internal sealed class SemanticReadRecipePolicy : IEntityReadRecipePolicy
{
    internal static readonly SemanticReadRecipePolicy Instance = new();

    public DatabaseProfileId Profile => DatabaseProfileId.Semantic;

    public bool SupportsContributionGuard => true;

    public void ValidateShape(EntityReadShape shape)
    {
        if (shape.Kind != "project" || shape.Fields is not { ValueKind: System.Text.Json.JsonValueKind.Array })
        {
            throw new ArgumentException("Unsupported Semantic read shape.");
        }
    }
}
