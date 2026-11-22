using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EntityForge.Db.Storage.Pages;

internal static class RedoPageRecovery
{
    internal static async ValueTask ApplyAsync(IReadOnlyList<RedoWalTransaction> transactions,
        IResizablePhysicalPageStore pages, CancellationToken cancellationToken)
    {
        long pageCount = await pages.GetPageCountAsync(cancellationToken).ConfigureAwait(false);
        foreach (RedoWalTransaction transaction in transactions)
        {
            foreach (IGrouping<long, RedoPageEntry> group in transaction.PageEntries
                         .Where(item => item.Kind == RedoPageEntryKind.Patch).GroupBy(item => item.PageId))
            {
                var pageId = new PhysicalPageId(group.Key);
                if (pageId.Value >= pageCount)
                {
                    await pages.EnsureCapacityAsync(pageId.Value + 1, cancellationToken).ConfigureAwait(false);
                    pageCount = pageId.Value + 1;
                }
                byte[] image = (await ReadAsync(pages, pageId, cancellationToken).ConfigureAwait(false)).ToArray();
                long pageLsn;
                bool reconstructed = false;
                try
                {
                    pageLsn = ReadPageLsn(image);
                }
                catch (PhysicalPageCorruptionException)
                {
                    RedoPageEntry protection = group.LastOrDefault(item =>
                        item.Offset == 0 && item.Bytes.Length == pages.PageSize)
                        ?? transaction.PageEntries.LastOrDefault(item =>
                            item.PageId == pageId.Value && item.Kind == RedoPageEntryKind.FullPageProtection)
                        ?? throw new RedoWalCorruptionException(
                            $"Page {pageId.Value} has redo patches without checkpoint-epoch protection.");
                    image = protection.Bytes.ToArray();
                    pageLsn = ReadPageLsn(image);
                    reconstructed = true;
                }
                if (!reconstructed && pageLsn >= transaction.Lsn)
                {
                    continue;
                }

                foreach (RedoPageEntry patch in group)
                {
                    patch.Bytes.Span.CopyTo(image.AsSpan(patch.Offset));
                }

                WritePageLsnAndChecksum(image, transaction.Lsn);
                await WriteAsync(pages, pageId, image, cancellationToken).ConfigureAwait(false);
            }
        }

        if (pages is IPhysicalPageRecoveryStore recovery)
        {
            await recovery.FlushRawAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await pages.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask<ReadOnlyMemory<byte>> ReadAsync(IResizablePhysicalPageStore pages,
        PhysicalPageId pageId, CancellationToken cancellationToken) =>
        pages is IPhysicalPageRecoveryStore recovery
            ? recovery.ReadRawAsync(pageId, cancellationToken)
            : pages.ReadAsync(pageId, cancellationToken);

    private static ValueTask WriteAsync(IResizablePhysicalPageStore pages, PhysicalPageId pageId,
        ReadOnlyMemory<byte> image, CancellationToken cancellationToken) =>
        pages is IPhysicalPageRecoveryStore recovery
            ? recovery.WriteRawAsync(pageId, image, cancellationToken)
            : pages.WriteAsync(pageId, image, cancellationToken);

    private static long ReadPageLsn(ReadOnlySpan<byte> image)
    {
        var pageId = new PhysicalPageId(BinaryPrimitives.ReadInt64LittleEndian(image[12..]));
        SlottedDataPage.Validate(image, pageId);
        return BinaryPrimitives.ReadInt64LittleEndian(image[28..]);
    }

    private static void WritePageLsnAndChecksum(Span<byte> image, long lsn)
    {
        BinaryPrimitives.WriteInt64LittleEndian(image[28..], lsn);
        BinaryPrimitives.WriteUInt32LittleEndian(image[44..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image[44..],
            BinaryPrimitives.ReadUInt32LittleEndian(SHA256.HashData(image)));
    }
}
