namespace EntityForge.Db.Semantic.Durability;

/// <summary>Conservative source-owner coverage, committed with source-home progress.
/// Null coverage means unknown; it never certifies absence.</summary>
public sealed class SemanticOwnerCoverage : IEquatable<SemanticOwnerCoverage>
{
    /// <summary>The maximum number of explicitly covered owners, 64.</summary>
    public const int MaximumOwners = 64;
    /// <summary>The maximum character length of routing and owner coordinates, 4,096.</summary>
    public const int MaximumCoordinateCharacters = 4096;

    /// <summary>Creates bounded coverage from a routing identity and distinct owner coordinates already sorted ordinally.</summary>
    public SemanticOwnerCoverage(string routing, IEnumerable<string> owners)
    {
        if (string.IsNullOrWhiteSpace(routing) || routing.Length > MaximumCoordinateCharacters)
        {
            throw new InvalidDataException("Invalid coverage routing identity.");
        }

        ArgumentNullException.ThrowIfNull(owners);
        var values = owners.ToArray();
        if (values.Length > MaximumOwners || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > MaximumCoordinateCharacters) ||
            !values.SequenceEqual(values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Invalid canonical owner coverage.");
        }

        Routing = routing;
        Owners = Array.AsReadOnly(values);
    }

    /// <summary>Gets the routing identity under which the owner set was derived.</summary>
    public string Routing
    {
        get;
    }

    /// <summary>Gets the canonical, distinct owner coordinates in ordinal order.</summary>
    public IReadOnlyList<string> Owners
    {
        get;
    }

    /// <summary>Compares the routing identity and ordered owner coordinates.</summary>
    public bool Equals(SemanticOwnerCoverage? other) => other is not null && Routing == other.Routing && Owners.SequenceEqual(other.Owners);

    /// <summary>Compares the routing identity and ordered owner coordinates.</summary>
    public override bool Equals(object? other) => other is SemanticOwnerCoverage coverage && Equals(coverage);

    /// <summary>Computes a hash from the routing identity and ordered owner coordinates.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Routing);
        foreach (var owner in Owners)
        {
            hash.Add(owner);
        }

        return hash.ToHashCode();
    }

    /// <summary>Writes a presence flag followed by bounded canonical owner coverage, preserving null as unknown coverage.</summary>
    public static void Write(BinaryWriter writer, SemanticOwnerCoverage? value)
    {
        writer.Write(value is not null);
        if (value is null)
        {
            return;
        }

        writer.Write(value.Routing);
        writer.Write(value.Owners.Count);
        foreach (var owner in value.Owners)
        {
            writer.Write(owner);
        }
    }

    /// <summary>Reads and validates bounded canonical coverage; returns null for the absent marker.</summary>
    public static SemanticOwnerCoverage? Read(BinaryReader reader)
    {
        byte flag = reader.ReadByte();
        if (flag == 0)
        {
            return null;
        }

        if (flag != 1)
        {
            throw new InvalidDataException("Invalid coverage presence flag.");
        }

        string routing = ReadCoordinate(reader);
        int count = reader.ReadInt32();
        if (count < 0 || count > MaximumOwners)
        {
            throw new InvalidDataException("Invalid coverage owner count.");
        }

        var owners = new string[count];
        for (int i = 0; i < count; i++)
        {
            owners[i] = ReadCoordinate(reader);
        }

        return new(routing, owners);
    }

    private static string ReadCoordinate(BinaryReader reader)
    {
        const int MaximumUtf8BytesPerCharacter = 4;
        int length = reader.Read7BitEncodedInt();
        if (length < 0 || length > MaximumCoordinateCharacters * MaximumUtf8BytesPerCharacter)
        {
            throw new InvalidDataException("Owner coverage coordinate exceeds its bound.");
        }

        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("Truncated owner coverage coordinate.");
        }

        return new System.Text.UTF8Encoding(false, true).GetString(bytes);
    }
}
