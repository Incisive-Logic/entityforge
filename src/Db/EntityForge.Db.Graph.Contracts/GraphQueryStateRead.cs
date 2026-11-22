using System.Security.Cryptography;

namespace EntityForge.Db.Graph.Contracts;

// Complete selector columns plus complete selected vertices. Unselected values
// cannot influence a single-node scalar mutation, but membership/absence can.
/// <summary>Tracks vertex membership, labels, selector properties and complete selected vertices for optimistic query validation.</summary>
public sealed record GraphQueryStateRead : GraphReadPredicate
{
    private const int FingerprintCharacters = SHA256.HashSizeInBytes * 2;

    /// <summary>Gets the selector property names whose values contribute for every observed vertex.</summary>
    public IReadOnlyList<string> Properties
    {
        get;
    }

    /// <summary>Gets the selected vertices whose complete property state contributes to the fingerprint.</summary>
    public IReadOnlyList<GraphVertexId> Subjects
    {
        get;
    }

    /// <summary>Gets the expected SHA-256 fingerprint of the captured dependency state.</summary>
    public string ExpectedFingerprint
    {
        get;
    }

    /// <summary>Creates a dependency from selector names, selected identities and an existing fingerprint.</summary>
    /// <param name="properties">Selector property names; captured and deduplicated.</param>
    /// <param name="subjects">Selected vertex identities; captured and deduplicated.</param>
    /// <param name="expectedFingerprint">A 64-character hexadecimal SHA-256 fingerprint.</param>
    /// <exception cref="ArgumentNullException">A sequence or fingerprint is null.</exception>
    /// <exception cref="ArgumentException">A property name, identity or fingerprint is invalid.</exception>
    public GraphQueryStateRead(IEnumerable<string> properties, IEnumerable<GraphVertexId> subjects, string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        Properties = Array.AsReadOnly(properties.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        Subjects = Array.AsReadOnly(subjects.Distinct().OrderBy(s => s.Value).ToArray());
        if (Properties.Any(String.IsNullOrWhiteSpace) || Subjects.Any(s => s.Value == Guid.Empty) ||
            expectedFingerprint.Length != FingerprintCharacters || !expectedFingerprint.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid query dependency.");
        }

        ExpectedFingerprint = expectedFingerprint;
    }

    /// <summary>Captures a query dependency over selector properties and the complete selected vertices.</summary>
    /// <param name="properties">Property names whose values contribute for every vertex.</param>
    /// <param name="subjects">Selected vertices whose complete properties contribute.</param>
    /// <param name="vertices">The observed vertex set, including membership and labels.</param>
    /// <returns>A read predicate retaining the selector, subjects and expected state fingerprint.</returns>
    public static GraphQueryStateRead Capture(IEnumerable<string> properties, IEnumerable<GraphVertexId> subjects, IEnumerable<GraphVertex> vertices)
    {
        var read = new GraphQueryStateRead(properties, subjects, new string('0', FingerprintCharacters));
        return new(read.Properties, read.Subjects, read.Fingerprint(vertices));
    }

    /// <summary>Computes the deterministic fingerprint of vertex membership, labels and selected property state.</summary>
    /// <param name="vertices">The vertex set to compare with the captured query dependency.</param>
    /// <returns>The uppercase SHA-256 fingerprint of the encoded dependency state.</returns>
    public string Fingerprint(IEnumerable<GraphVertex> vertices)
    {
        var columns = Properties.ToHashSet(StringComparer.Ordinal);
        var selected = Subjects.ToHashSet();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var vertex in vertices.OrderBy(v => v.Id.Value))
        {
            Graph.GraphTransactionCodec.WriteMutation(writer, new CreateVertex(vertex.Id, vertex.Labels.Order(StringComparer.Ordinal).ToArray()));
            foreach (var property in vertex.Properties.Where(p => selected.Contains(vertex.Id) || columns.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Graph.GraphTransactionCodec.WriteMutation(writer, new SetVertexProperty(vertex.Id, property.Key, property.Value));
            }
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static string PropertyResource(string property) => "query:property:" + property;

    internal const string MembershipResource = "query:membership";
}
