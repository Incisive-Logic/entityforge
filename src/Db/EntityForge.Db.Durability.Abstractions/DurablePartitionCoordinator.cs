using System.Text;

namespace EntityForge.Db.Durability;

/// <summary>Immutable home authority selection carried by its native preparation.</summary>
/// <remarks>Commitment of that exact native preparation is the commit decision.
/// Aborted evidence is the abort decision. Hosting validates the complete manifest;
/// the kernel arbitrates its immutable application identity and recovery boundary.</remarks>
public sealed class DurablePartitionCoordinator
{
    /// <summary>The maximum encoded home manifest size, one mebibyte.</summary>
    public const int MaximumManifestBytes = 1024 * 1024;
    /// <summary>The maximum UTF-8 authority identity size, 1,024 bytes.</summary>
    public const int MaximumAuthorityBytes = 1024;
    /// <summary>The fixed framing and application-hash overhead of encoded coordinator evidence.</summary>
    public const int FramingBytes = sizeof(int) * 2 + 32;
    /// <summary>The maximum encoded coordinator evidence size including framing.</summary>
    public const int MaximumEncodedBytes = MaximumManifestBytes + MaximumAuthorityBytes + FramingBytes;

    /// <summary>Creates bounded coordinator evidence, copying the exact manifest and normalizing its application descriptor hash.</summary>
    public DurablePartitionCoordinator(string authority, string applicationDescriptor, ReadOnlyMemory<byte> manifest)
    {
        if (string.IsNullOrWhiteSpace(authority) || Encoding.UTF8.GetByteCount(authority) > MaximumAuthorityBytes ||
            applicationDescriptor is null || applicationDescriptor.Length != 64 || !applicationDescriptor.All(Uri.IsHexDigit) ||
            manifest.IsEmpty || manifest.Length > MaximumManifestBytes)
        {
            throw new ArgumentException("Invalid bounded native coordinator selection.");
        }

        Authority = authority;
        ApplicationDescriptor = applicationDescriptor.ToUpperInvariant();
        Manifest = manifest.ToArray();
    }

    /// <summary>Gets the home authority identity selected for this operation.</summary>
    public string Authority
    {
        get;
    }

    /// <summary>Gets the uppercase application descriptor hash.</summary>
    public string ApplicationDescriptor
    {
        get;
    }

    /// <summary>Gets the exact owned home manifest bytes.</summary>
    public ReadOnlyMemory<byte> Manifest
    {
        get;
    }

    /// <summary>Gets the total encoded size including UTF-8 authority bytes and framing.</summary>
    public int EncodedLength => checked(FramingBytes + Encoding.UTF8.GetByteCount(Authority) + Manifest.Length);

    /// <summary>Checks exact authority, descriptor and manifest equality.</summary>
    public bool Matches(DurablePartitionCoordinator other) => Authority == other.Authority &&
        ApplicationDescriptor == other.ApplicationDescriptor && Manifest.Span.SequenceEqual(other.Manifest.Span);

    /// <summary>Encodes the bounded authority, descriptor hash and manifest using the coordinator wire format.</summary>
    public byte[] Encode()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        byte[] authority = Encoding.UTF8.GetBytes(Authority);
        writer.Write(authority.Length);
        writer.Write(authority);
        writer.Write(Convert.FromHexString(ApplicationDescriptor));
        writer.Write(Manifest.Length);
        writer.Write(Manifest.Span);
        return bytes.ToArray();
    }

    /// <summary>Decodes coordinator evidence, rejecting malformed lengths, invalid UTF-8 and trailing bytes.</summary>
    public static DurablePartitionCoordinator Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length < FramingBytes || bytes.Length > MaximumEncodedBytes)
        {
            throw new InvalidDataException("Invalid coordinator evidence length.");
        }

        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var reader = new BinaryReader(stream);
        int authorityLength = reader.ReadInt32();
        if (authorityLength is <= 0 or > MaximumAuthorityBytes || authorityLength > stream.Length - stream.Position - (FramingBytes - sizeof(int)))
        {
            throw new InvalidDataException("Invalid coordinator authority length.");
        }

        string authority = new UTF8Encoding(false, true).GetString(reader.ReadBytes(authorityLength));
        string descriptor = Convert.ToHexString(reader.ReadBytes(32));
        int length = reader.ReadInt32();
        if (length is <= 0 or > MaximumManifestBytes || length != stream.Length - stream.Position)
        {
            throw new InvalidDataException("Invalid coordinator manifest length.");
        }

        return new(authority, descriptor, reader.ReadBytes(length));
    }
}
