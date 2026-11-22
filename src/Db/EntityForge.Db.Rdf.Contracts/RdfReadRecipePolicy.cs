using EntityForge.Db.Mapping;

namespace EntityForge.Db.Rdf;

internal sealed class RdfReadRecipePolicy : IEntityReadRecipePolicy
{
    internal static readonly RdfReadRecipePolicy Instance = new();

    public DatabaseProfileId Profile => DatabaseProfileId.Rdf;

    public bool SupportsContributionGuard => false;

    public void ValidateShape(EntityReadShape shape)
    {
        if (shape.Kind != "subject" || shape.Links.Length != 0)
        {
            throw new ArgumentException("Unsupported Rdf read shape.");
        }
    }
}
