using EntityForge.Db.Storage.Pages;

namespace EntityForge.Db.Semantic;

internal static class SemanticLookupRoles
{
    internal static readonly AppendMergeLookupRole AtomDirectory = AppendMergeLookupRole.Register("atom_directory");
    internal static readonly AppendMergeLookupRole AtomPayload = AppendMergeLookupRole.Register("atom_payload");
}
