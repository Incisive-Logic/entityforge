using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace EntityForge.Db.Prototypes.Pages;

public sealed record FilesystemPublicationProbeResult(int SchemaVersion, string OperatingSystem,
    int ReplacementCount, int ObservedReadCount, int InvalidReadCount, int MissingReadCount,
    int TransientOpenRetryCount,
    bool OpenReaderPreservedPredecessorImage, bool NewReaderObservedReplacement,
    bool PublicationCoordinated, bool ManagedDirectoryFlushAvailable)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

public sealed class FilesystemPublicationProbe
{
    public const int ResultSchemaVersion = 1;
    private const int ImageSize = 16 * 1024;
    private const int ReplacementCount = 100;

    public async Task<FilesystemPublicationProbeResult> RunAsync(bool coordinatePublication = false,
        CancellationToken cancellationToken = default)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"entityforge-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string target = Path.Combine(directory, "checkpoint.dat");
            byte[] predecessor = Image(0);
            await WriteDurablyAsync(target, predecessor, cancellationToken).ConfigureAwait(false);
            await using var predecessorReader = new FileStream(target, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, ImageSize, FileOptions.Asynchronous);
            var validDigests = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal)
            {
                [Digest(predecessor)] = 0
            };
            int invalidReads = 0;
            int missingReads = 0;
            int observedReads = 0;
            int transientOpenRetries = 0;
            using var stop = new CancellationTokenSource();
            using var publicationGate = new SemaphoreSlim(1, 1);
            Task observer = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        if (coordinatePublication)
                        {
                            await publicationGate.WaitAsync(stop.Token).ConfigureAwait(false);
                        }

                        byte[] observed;
                        try
                        {
                            observed = await ReadSharedAsync(target, stop.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (coordinatePublication)
                            {
                                publicationGate.Release();
                            }
                        }
                        Interlocked.Increment(ref observedReads);
                        if (observed.Length != ImageSize || !validDigests.ContainsKey(Digest(observed)))
                        {
                            Interlocked.Increment(ref invalidReads);
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        Interlocked.Increment(ref missingReads);
                    }
                    catch (IOException) when (!stop.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref transientOpenRetries);
                        await Task.Yield();
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {

                    }
                }
            }, cancellationToken);

            byte[] replacement = predecessor;
            for (int generation = 1; generation <= ReplacementCount; generation++)
            {
                replacement = Image(generation);
                validDigests.TryAdd(Digest(replacement), 0);
                string temporary = target + ".new";
                await WriteDurablyAsync(temporary, replacement, cancellationToken).ConfigureAwait(false);
                if (coordinatePublication)
                {
                    await publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    File.Replace(temporary, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                finally
                {
                    if (coordinatePublication)
                    {
                        publicationGate.Release();
                    }
                }
            }
            stop.Cancel();
            await observer.ConfigureAwait(false);

            var oldImage = new byte[ImageSize];
            int oldRead = await predecessorReader.ReadAsync(oldImage, cancellationToken).ConfigureAwait(false);
            byte[] newImage = await ReadSharedAsync(target, cancellationToken).ConfigureAwait(false);
            return new(ResultSchemaVersion, Environment.OSVersion.ToString(), ReplacementCount,
                observedReads, invalidReads, missingReads,
                transientOpenRetries,
                oldRead == ImageSize && predecessor.SequenceEqual(oldImage), replacement.SequenceEqual(newImage),
                coordinatePublication,
                ManagedDirectoryFlushAvailable: false);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static async Task WriteDurablyAsync(string path, byte[] image, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            ImageSize, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(image, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadSharedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, ImageSize, FileOptions.Asynchronous);
        var image = new byte[stream.Length];
        await stream.ReadExactlyAsync(image, cancellationToken).ConfigureAwait(false);
        return image;
    }

    private static byte[] Image(int generation)
    {
        var image = new byte[ImageSize];
        var random = new Random(generation);
        random.NextBytes(image);
        return image;
    }

    private static string Digest(byte[] image) => Convert.ToHexString(SHA256.HashData(image));
}
