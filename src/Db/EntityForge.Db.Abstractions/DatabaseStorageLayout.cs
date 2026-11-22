using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace EntityForge.Db;

/// <summary>Identifies the immutable physical ownership contract, independently of shard placement.</summary>
public enum DatabaseStorageLayoutKind : byte
{
    /// <summary>Profile-owned records: Graph vertices and explicit edge replicas, RDF subjects, Semantic owners.</summary>
    ProfileOwned = 1,

    /// <summary>Graph topology is separate from entity-owned property bags.</summary>
    GraphTopologyProperties = 2
}

/// <summary>A creation-time ownership contract. An opener must also support the selected profile/layout pair.</summary>
public sealed record DatabaseStorageLayout
{
    /// <summary>The supported format version for profile-owned records.</summary>
    public const int ProfileOwnedVersion = 1;

    /// <summary>The supported format version for separately owned graph topology and properties.</summary>
    public const int GraphTopologyPropertiesVersion = 1;

    /// <summary>Gets the default profile-owned layout descriptor.</summary>
    public static DatabaseStorageLayout Current { get; } = new(DatabaseStorageLayoutKind.ProfileOwned, ProfileOwnedVersion);

    /// <summary>Gets the layout descriptor separating graph topology from entity-owned property bags.</summary>
    public static DatabaseStorageLayout GraphTopologyProperties { get; } = new(DatabaseStorageLayoutKind.GraphTopologyProperties, GraphTopologyPropertiesVersion);

    /// <summary>The number of bytes written by <see cref="Write"/> for a layout descriptor.</summary>
    public const int EncodedLength = sizeof(byte) + sizeof(int);

    /// <summary>Creates a descriptor for a supported ownership contract and format version.</summary>
    /// <param name="kind">The physical ownership contract.</param>
    /// <param name="version">The supported format version for that contract.</param>
    /// <exception cref="InvalidDataException">The kind or its version is unsupported.</exception>
    [JsonConstructor]
    public DatabaseStorageLayout(DatabaseStorageLayoutKind kind, int version)
    {
        if (kind switch
        {
            DatabaseStorageLayoutKind.ProfileOwned => version != ProfileOwnedVersion,
            DatabaseStorageLayoutKind.GraphTopologyProperties => version != GraphTopologyPropertiesVersion,
            _ => true
        })
        {
            throw new InvalidDataException($"Unsupported database storage layout: {kind}, version {version}.");
        }

        Kind = kind;
        Version = version;
    }

    /// <summary>Gets the physical ownership contract.</summary>
    public DatabaseStorageLayoutKind Kind
    {
        get;
    }

    /// <summary>Gets the format version within the selected layout kind.</summary>
    public int Version
    {
        get;
    }

    /// <summary>Gets the stable uppercase hexadecimal SHA-256 fingerprint of the layout contract identifier.</summary>
    /// <remarks>This identifies the ownership contract, not the contents or integrity of a database.</remarks>
    [JsonIgnore]
    public string Fingerprint => Kind == DatabaseStorageLayoutKind.ProfileOwned ? CurrentFingerprint : PropertyFingerprint;

    private static readonly string PropertyFingerprint = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("entityforge.storage-layout.graph-topology-properties.v1")));

    private static readonly string CurrentFingerprint = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("entityforge.storage-layout.profile-owned.v1")));

    /// <summary>Writes this storage-layout discriminator and format version in their binary contract order.</summary>
    /// <param name="writer">Destination writer; ownership remains with the caller.</param>
    public void Write(BinaryWriter writer)
    {
        writer.Write((byte)Kind);
        writer.Write(Version);
    }

    /// <summary>Reads and validates a binary storage-layout discriminator and format version.</summary>
    /// <param name="reader">Reader positioned at the layout record; ownership remains with the caller.</param>
    /// <returns>The decoded layout descriptor.</returns>
    /// <exception cref="EndOfStreamException">The record is truncated.</exception>
    /// <exception cref="InvalidDataException">The encoded kind or version is unsupported.</exception>
    public static DatabaseStorageLayout Read(BinaryReader reader) => new((DatabaseStorageLayoutKind)reader.ReadByte(), reader.ReadInt32());
}
