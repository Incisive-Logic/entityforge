using System.Buffers.Binary;
using System.Security.Cryptography;
using EntityForge.Db.Rdf.Contracts;

namespace EntityForge.Db.Rdf;

/// <summary>Identifies one resumable RDF source document independently of a process lifetime.</summary>
public readonly record struct RdfDocumentIngestId
{
    /// <summary>Creates a document ingest identity.</summary>
    public RdfDocumentIngestId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A document ingest identity is required.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the durable identity value.</summary>
    public Guid Value
    {
        get;
    }
}

/// <summary>Describes one bounded, independently retryable page of an RDF document.</summary>
public sealed record RdfDocumentPage
{
    /// <summary>The maximum number of quads accepted in one atomic page.</summary>
    public const int MaximumQuadCount = 4_096;

    /// <summary>Creates a document page.</summary>
    public RdfDocumentPage(long pageNumber, IReadOnlyList<RdfQuad> quads)
    {
        if (pageNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber));
        }

        ArgumentNullException.ThrowIfNull(quads);
        if (quads.Count == 0 || quads.Count > MaximumQuadCount)
        {
            throw new ArgumentOutOfRangeException(nameof(quads),
                $"An RDF document page must contain between 1 and {MaximumQuadCount} quads.");
        }

        PageNumber = pageNumber;
        Quads = quads.Select(quad => (quad ?? throw new ArgumentException(
            "An RDF document page cannot contain a null quad.", nameof(quads))).Validate()).ToArray();
    }

    /// <summary>Gets the zero-based source page number.</summary>
    public long PageNumber
    {
        get;
    }

    /// <summary>Gets the quads asserted by this page.</summary>
    public IReadOnlyList<RdfQuad> Quads
    {
        get;
    }
}

/// <summary>Reports the durable outcome of committing one RDF document page.</summary>
/// <param name="DocumentId">Durable identity of the source document.</param>
/// <param name="PageNumber">Zero-based page number within the document.</param>
/// <param name="Version">Database version associated with the page commit.</param>
/// <param name="Outcome">Whether the page was newly committed or resolved as a retry.</param>
public sealed record RdfDocumentPageCommit(
    RdfDocumentIngestId DocumentId,
    long PageNumber,
    DatabaseVersion Version,
    DatabaseCommitOutcome Outcome);

internal static class RdfDocumentPageIdentity
{
    private static ReadOnlySpan<byte> Domain => "EntityForge.Db.Rdf.DocumentPage.v1"u8;

    public static Guid Create(RdfDocumentIngestId documentId, long pageNumber)
    {
        Span<byte> input = stackalloc byte[Domain.Length + 16 + sizeof(long)];
        Domain.CopyTo(input);
        documentId.Value.TryWriteBytes(input[Domain.Length..]);
        BinaryPrimitives.WriteInt64BigEndian(input[(Domain.Length + 16)..], pageNumber);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }
}
