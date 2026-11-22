using System.Buffers.Binary;
using EntityForge.Db.Storage;

namespace EntityForge.Db.Storage.Pages;

/// <summary>Initializes the selected redo format with a durable allocation-floor marker.</summary>
public sealed class GroupedRedoRestoreLogInitializer : IDatabaseRestoreLogInitializer
{
    private const int PageIdentityOffset = 12;
    private const int PageLsnOffset = 28;

    /// <summary>Creates a new durable WAL whose allocation floor covers the supplied floor and valid preserved page LSNs.</summary>
    /// <param name="path">New WAL path in an unpublished restore staging directory; must not already exist.</param>
    /// <param name="physicalImagePath">Restored physical image with a length divisible by the default page size.</param>
    /// <param name="allocationFloor">Nonnegative minimum redo allocation floor.</param>
    /// <param name="cancellationToken">Cancels image scanning or WAL initialization.</param>
    /// <remarks>Requires exclusive restore coordination. Invalid or unallocated page images do not contribute LSNs;
    /// this scan is not an integrity validation of the restored database. Failed initialization may leave a staging file.</remarks>
    public async ValueTask InitializeAsync(string path, string physicalImagePath, long allocationFloor,
        CancellationToken cancellationToken = default)
    {
        if (allocationFloor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(allocationFloor));
        }

        await using var pages = new FileStream(physicalImagePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, PhysicalPageFileOptions.DefaultPageSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (pages.Length % PhysicalPageFileOptions.DefaultPageSize != 0)
        {
            throw new InvalidDataException("The restored physical image ends inside a page.");
        }

        byte[] image = new byte[PhysicalPageFileOptions.DefaultPageSize];
        while (pages.Position < pages.Length)
        {
            await pages.ReadExactlyAsync(image, cancellationToken).ConfigureAwait(false);
            long identity = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(PageIdentityOffset));
            if (identity < 0)
            {
                continue;
            }
            // Access-path stores may use relative identities. Match redo's validity check.
            try
            {
                SlottedDataPage.Validate(image, new(identity));
            }
            catch (PhysicalPageCorruptionException)
            {
                // Unallocated/retired regions may contain invalid images. Redo never skips an
                // invalid image based on its LSN; only valid preserved pages constrain this floor.
                continue;
            }
            allocationFloor = Math.Max(allocationFloor, BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(PageLsnOffset)));
        }
        if (allocationFloor == long.MaxValue)
        {
            throw new InvalidDataException("The restored redo allocation space is exhausted.");
        }

        await GroupedRedoDatabaseCommitLog.InitializeRestoredFloorAsync(path, allocationFloor, cancellationToken)
            .ConfigureAwait(false);
    }
}
