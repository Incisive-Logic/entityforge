using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Distributed.Hosting;

// Each receiving boundary owns mutable inputs before registering accepted work.
// The same representation is used by all profiles; it contains no serving proof.
internal sealed record PartitionQueryOwnedSelection(PartitionQueryExchangeRequest Value, string? EntityIdentity)
{
    internal static PartitionQueryOwnedSelection Capture(PartitionQueryExchangeRequest value)
    {
        string? entity = value.EntityQuery is null ? null : JsonSerializer.Serialize(value.EntityQuery, PartitionQueryJson.Options);
        return new(value with
        {
            EntityQuery = entity is null ? null : JsonSerializer.Deserialize<PortableEntityQueryRequest>(entity, PartitionQueryJson.Options)!,
            GraphFrontier = value.GraphFrontier is not { } frontier ? null : frontier with
            {
                Vertices = frontier.Vertices?.ToArray(),
                Edges = frontier.Edges?.ToArray()
            },
            RdfFrontier = value.RdfFrontier is not { } rdf ? null : rdf with
            {
                Bindings = rdf.Bindings.Select(row => new Dictionary<string, EntityForge.Db.Rdf.Contracts.RdfTerm>(row, StringComparer.Ordinal)).ToArray()
            },
            SemanticFrontier = value.SemanticFrontier is not { } semantic ? null : semantic with
            {
                Entities = semantic.Entities?.ToArray()
            }
        }, entity);
    }
}

internal sealed class PartitionQueryInterpretation(PartitionQueryOwnedSelection owned)
{
    internal PartitionOperatorInterpretation OperatorIdentity
    {
        get;
    }
    = new(owned.Value.Language,
        owned.Value.Statement, owned.EntityIdentity, owned.Value.CompleteQuery, owned.Value.GraphFragments, owned.Value.ProfileFragments, owned.Value.IncludeSupportingGraph, owned.Value.ContinueLocal);

    private readonly PartitionQueryExchangeRequest _value = owned.Value with { Offset = 0, GraphFrontier = null, RdfFrontier = null, SemanticFrontier = null };
    private readonly string? _entity = owned.EntityIdentity;
    private readonly bool _otherFrontier = owned.Value.GraphFrontier is not null || owned.Value.RdfFrontier is not null;
    private readonly bool _semanticFrontier = owned.Value.SemanticFrontier is not null;
    private readonly int _semanticLastStage = owned.Value.SemanticFrontier is not null && owned.Value.EntityQuery is { } query
        ? PartitionSemanticFrontier.Depth(query.Projection) + 1 : -1;

    private string? _fingerprint;

    internal string Fingerprint => _fingerprint ??= Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_value, PartitionQueryJson.Options))));

    internal void Require(PartitionQueryOwnedSelection owned)
    {
        var value = owned.Value;
        if ((!_otherFrontier && (value.GraphFrontier is not null || value.RdfFrontier is not null)) ||
            (value.SemanticFrontier is not null) != _semanticFrontier ||
            (value.SemanticFrontier is { } semantic && (semantic.Stage < 0 || semantic.Stage > _semanticLastStage ||
                value.GraphFrontier is not null || value.RdfFrontier is not null)))
        {
            throw new InvalidDataException("Query job changed its Semantic frontier contract.");
        }

        if (value.Language != _value.Language || value.Statement != _value.Statement || owned.EntityIdentity != _entity ||
            value.CompleteQuery != _value.CompleteQuery || value.GraphFragments != _value.GraphFragments ||
            value.ContinueLocal != _value.ContinueLocal || value.ProfileFragments != _value.ProfileFragments || value.IncludeSupportingGraph != _value.IncludeSupportingGraph)
        {
            throw new InvalidDataException("Query job identity was reused with another interpretation.");
        }
    }
}

// Fixed interpretation is already checked by the owning job. Only the frontier
// distinguishes work in its workspace; offsets select pages of that same work.
internal sealed class PartitionQueryWorkKey : IEquatable<PartitionQueryWorkKey>
{
    private readonly PartitionGraphQueryFrontier? _frontier;
    private readonly int _hash;
    private readonly string? _rdf;
    private readonly string? _semantic;

    internal PartitionQueryWorkKey(PartitionGraphQueryFrontier? frontier, EntityForge.Db.Rdf.RdfBindingFrontier? rdf = null, PartitionSemanticFrontier? semantic = null)
    {
        _semantic = semantic is null ? null : JsonSerializer.Serialize(semantic, PartitionQueryJson.Options);
        _rdf = rdf is null ? null : JsonSerializer.Serialize(new
        {
            rdf.Pattern,
            Bindings = rdf.Bindings.Select(row => row.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray()).ToArray()
        }, PartitionQueryJson.Options);
        _frontier = frontier is null ? null : frontier with
        {
            Vertices = frontier.Vertices?.ToArray(),
            Edges = frontier.Edges?.ToArray()
        };
        frontier = _frontier;
        var hash = new HashCode();
        hash.Add(frontier is not null);
        hash.Add(_rdf, StringComparer.Ordinal);
        hash.Add(_semantic, StringComparer.Ordinal);
        if (frontier is not null)
        {
            hash.Add(frontier.Stage);
            hash.Add(frontier.RoleOperator);
            Add(frontier.Vertices);
            Add(frontier.Edges);
        }
        _hash = hash.ToHashCode();
        void Add(Guid[]? values)
        {
            hash.Add(values is not null);
            if (values is not null)
            {
                foreach (var value in values)
                {
                    hash.Add(value);
                }
            }
        }
    }

    public bool Equals(PartitionQueryWorkKey? other) => other is not null && _rdf == other._rdf && _semantic == other._semantic &&
        (_frontier is null ? other._frontier is null : other._frontier is { } right &&
            _frontier.Stage == right.Stage && _frontier.RoleOperator == right.RoleOperator && Same(_frontier.Vertices, right.Vertices) && Same(_frontier.Edges, right.Edges));

    private static bool Same(Guid[]? left, Guid[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    public override bool Equals(object? other) => other is PartitionQueryWorkKey key && Equals(key);

    public override int GetHashCode() => _hash;

    internal long IdentityBytes => checked((_rdf is null ? 0 : Encoding.UTF8.GetByteCount(_rdf)) + (_semantic is null ? 0 : Encoding.UTF8.GetByteCount(_semantic)) + 16L * ((_frontier?.Vertices?.Length ?? 0) + (_frontier?.Edges?.Length ?? 0)));
}
