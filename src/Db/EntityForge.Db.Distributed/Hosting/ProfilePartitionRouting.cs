using EntityForge.Db.Mapping;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Semantics;
using EntityForge.Db.Distributed.Sharding;

namespace EntityForge.Db.Distributed.Hosting;

// Stable owner encoding shared by profile planning and partition execution.
internal static class ProfilePartitionRouting
{
    internal static string RdfOwner(RdfTerm term) => term switch
    {
        RdfIri value => Field('I', value.Value),
        RdfBlankNode value => Field('B', value.Identifier),
        RdfTripleTerm value => $"T{RdfOwner(value.Subject)}{Field('P', value.Predicate.Value)}{RdfObject(value.Object)}",
        _ => throw new InvalidDataException("An RDF mutation subject must be a resource or quoted triple.")
    };

    private static string RdfObject(RdfTerm term) => term switch
    {
        RdfLiteral value => Field('L', value.LexicalForm) + Field('D', value.Datatype ?? String.Empty) +
                            Field('A', value.Language ?? String.Empty),
        _ => RdfOwner(term)
    };

    private static string Field(char kind, string value) => $"{kind}{value.Length}:{value}";

    internal static string SemanticOwner(SemanticEntityIdentity identity) => SemanticEntityPlacement.OwnerKey(identity);

    // URI-valued domain records use Uri.Equals, unlike SemanticTerm's ordinal
    // AbsoluteUri comparison. Omit ignored fragments/userinfo. File-path case
    // folding must preserve native equality: these keys also identify durable progress.
    internal static string SemanticUri(Uri value) => SemanticEntityPlacement.SemanticUri(value);

    internal static LogicalShardId SemanticHome(DistributedRoutingBinding routing, Uri source, Uri partition)
    {
        string text = partition.AbsoluteUri;
        if (text.StartsWith(SemanticEntityPlacement.SourcePrefix, StringComparison.Ordinal))
        {
            string key = text[SemanticEntityPlacement.SourcePrefix.Length..];
            const int Sha256HexLength = 64;
            if (key.Length != Sha256HexLength || key.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            {
                throw new InvalidDataException("Invalid mapped source placement key.");
            }

            return routing.Route(DistributedDatabaseProfile.Semantic, key);
        }
        return routing.Route(DistributedDatabaseProfile.Semantic, "source-home.v2:" + EntityIdentityEncoding.Hash("semantic-source-home/v2", SemanticUri(source), SemanticUri(partition)));
    }
}
