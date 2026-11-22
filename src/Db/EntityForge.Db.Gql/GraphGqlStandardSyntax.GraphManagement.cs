using Antlr4.Runtime;
using EntityForge.Db.Query.Syntax.Gql;

namespace EntityForge.Db.Gql;

internal enum GqlGraphManagementKind
{
    Create,
    CreateIfAbsent,
    Replace,
    Drop,
    DropIfPresent
}

internal sealed record GqlGraphManagementCommand(string Name, GqlGraphManagementKind Kind);

internal static partial class GraphGqlStandardSyntax
{
    internal static GqlGraphManagementCommand ParseGraphManagement(string source)
    {
        var root = GqlSyntaxParser.Parse(source, out var tokens);
        var creates = Descendants<GQLParser.CreateGraphStatementContext>(root).ToArray();
        var drops = Descendants<GQLParser.DropGraphStatementContext>(root).ToArray();
        if (creates.Length + drops.Length != 1)
        {
            throw new NotSupportedException("Graph management accepts one standalone CREATE GRAPH or DROP GRAPH statement.");
        }
        ParserRuleContext command = creates.Length == 1 ? creates[0] : drops[0];
        var visible = tokens.GetTokens().Where(token => token.Channel == TokenConstants.DefaultChannel && token.Type != TokenConstants.EOF).ToArray();
        if (command.Start.TokenIndex != visible[0].TokenIndex || command.Stop.TokenIndex != visible[^1].TokenIndex)
        {
            throw new NotSupportedException("Graph management cannot be combined with other statements, session commands or transaction commands yet.");
        }
        GQLParser.CatalogGraphParentAndNameContext identity;
        GqlGraphManagementKind kind;
        if (creates.Length == 1)
        {
            var create = creates[0];
            if (create.openGraphType() is null || create.graphSource() is not null)
            {
                throw new NotSupportedException("Managed graph creation currently requires an open ANY graph type without a copy source.");
            }
            identity = create.catalogGraphParentAndName();
            kind = create.REPLACE() is not null ? GqlGraphManagementKind.Replace :
                create.IF() is not null ? GqlGraphManagementKind.CreateIfAbsent : GqlGraphManagementKind.Create;
        }
        else
        {
            identity = drops[0].catalogGraphParentAndName();
            kind = drops[0].IF() is not null ? GqlGraphManagementKind.DropIfPresent : GqlGraphManagementKind.Drop;
        }
        string name = identity.catalogObjectParentReference() is { } parent
            ? QualifiedLocalGraphKey(parent, identity.graphName().GetText())
            : GraphGqlIdentifiers.DecodeIdentifier(identity.graphName().GetText());
        if (!IsCanonicalLocalGraphKey(name))
        {
            throw new ArgumentException("The graph identity exceeds the supported identifier bounds.");
        }
        return new(name, kind);
    }
}
