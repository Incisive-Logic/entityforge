namespace EntityForge.Db;

/// <summary>Controls which clock may allocate native commit versions.</summary>
public enum DatabaseVersioningMode
{
    /// <summary>Existing local allocation, or external allocation under exclusive distributed enrollment.</summary>
    LocalOrAuthoritative = 0,

    /// <summary>
    /// All native versions are allocated by the partition kernel, for both local and coordinated operations.
    /// This persisted storage contract is not a per-save execution option. The distributed runtime
    /// selects the execution path; externally allocated versions cannot enter this kernel.
    /// </summary>
    PartitionLocal = 1
}

// An absent extension preserves the existing ordinary format byte for byte.
// Older readers reject partition records as trailing data rather than adopting them.
internal static class DatabaseVersioningFormat
{
    internal const int ExtensionLength = 5;

    internal static int Length(DatabaseVersioningMode mode) => mode switch
    {
        DatabaseVersioningMode.LocalOrAuthoritative => 0,
        DatabaseVersioningMode.PartitionLocal => ExtensionLength,
        _ => throw new InvalidDataException("Unsupported database versioning mode.")
    };

    internal static void Write(Span<byte> destination, DatabaseVersioningMode mode)
    {
        if (destination.Length != Length(mode))
        {
            throw new InvalidDataException("Invalid versioning extension size.");
        }

        if (destination.IsEmpty)
        {
            return;
        }

        "EFVM"u8.CopyTo(destination);
        destination[4] = (byte)mode;
    }

    internal static DatabaseVersioningMode Read(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            return DatabaseVersioningMode.LocalOrAuthoritative;
        }

        if (content.Length != ExtensionLength || !content[..4].SequenceEqual("EFVM"u8) ||
            content[4] != (byte)DatabaseVersioningMode.PartitionLocal)
        {
            throw new InvalidDataException("Invalid database versioning extension.");
        }

        return DatabaseVersioningMode.PartitionLocal;
    }
}
