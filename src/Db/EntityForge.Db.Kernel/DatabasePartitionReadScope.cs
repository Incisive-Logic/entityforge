
namespace EntityForge.Db.Kernel;

/// <summary>Owned profile resources for a bounded projection, not an unrestricted database cut.</summary>
/// <remarks>The receiving profile must derive the complete resources from the actual query,
/// including absence. These resource names are not user-supplied permission or authority.</remarks>
public sealed class DatabasePartitionReadScope
{
    /// <summary>The maximum number of resource identities in a scoped read, 256.</summary>
    public const int MaximumResources = 256;
    private const int MaximumResourceCharacters = 32 * 1024;
    private const int MaximumEncodedBytes = 256 * 1024;

    internal HashSet<string> Resources
    {
        get;
    }

    /// <summary>Gets the profile that interprets the resource identities.</summary>
    public DatabaseProfileId Profile
    {
        get;
    }

    /// <summary>Gets the fingerprint of the profile and complete ordered resource set.</summary>
    public string Fingerprint
    {
        get;
    }

    /// <summary>Creates a bounded, owned read-resource scope; rejects unknown profiles, empty resources and count or byte-limit violations.</summary>
    public DatabasePartitionReadScope(DatabaseProfileId profile, IEnumerable<string> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!Enum.IsDefined(profile))
        {
            throw new NotSupportedException("Unknown database profile.");
        }

        var owned = resources.Take(MaximumResources + 1).ToArray();
        if (owned.Length is < 1 or > MaximumResources || owned.Any(r => String.IsNullOrEmpty(r) || r.Length > MaximumResourceCharacters))
        {
            throw new ArgumentException("Invalid bounded read resources.");
        }

        Resources = new(owned, StringComparer.Ordinal);
        Profile = profile;
        using var hash = new DatabaseBinaryFingerprint("partition-read-scope/v2");
        hash.Long((int)Profile);
        hash.Long(Resources.Count);
        foreach (string resource in Resources.Order(StringComparer.Ordinal))
        {
            hash.Text(resource);
            if (hash.Bytes > MaximumEncodedBytes)
            {
                throw new ArgumentException("Read scope exceeds its byte bound.");
            }
        }
        Fingerprint = hash.Finish();
    }
}
