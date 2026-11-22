using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;

namespace EntityForge.Utility
{
    /// <summary>Describes an entry inside an archive or other indexed file container.</summary>
    public class ContainerFileInfo
    {
        /// <summary>Gets or sets the entry's address within its container.</summary>
        public string SubAddress
        {
            get; set;
        }

        /// <summary>Gets or sets the entry's recorded modification time.</summary>
        public DateTime LastModified
        {
            get; set;
        }

        /// <summary>Returns the entry's container subaddress.</summary>
        public override string ToString()
        {
            return SubAddress;
        }
    }

    /// <summary>Provides path, archive-entry and embedded-resource access helpers.</summary>
    public static class FileHelper
    {
        private const string FileTimeStampFormat = "yyyyMMddHHmmssfff";
        /// <summary>The extension used for temporary files.</summary>
        public const string TempFileExtension = "tmp";
        private const string ZipArchiveExtension = "zip";
        private const string ZipNameCriteria = "(name = {0})";
        private const string ZipOrOperator = " OR ";

        // Containers are considered to be files that contain other files and are indexable - for example a compressed archive
        /// <summary>The case-insensitive set of recognized container extensions; initially contains ZIP.</summary>
        public static readonly HashSet<string> ContainerFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ZipArchiveExtension
        };

        /// <summary>Gets semicolon-separated file wildcards for recognized container extensions.</summary>
        public static string ContainerMatchWildcard
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                int extensionIndex = 0;

                foreach (string fileExtension in ContainerFileExtensions)
                {
                    sb.Append($"*.{fileExtension}");
                    if (extensionIndex != (ContainerFileExtensions.Count - 1))
                    {
                        sb.Append(";");
                    }

                    extensionIndex++;
                }

                return sb.ToString();
            }
        }

        // Appends a date time stamp (UTC) to the file name, before the file extension
        /// <summary>Inserts the current UTC timestamp before a filename's extension, omitting its directory.</summary>
        public static string GetFileNameWithTimeStamp(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path, nameof(path));
            return Path.GetFileNameWithoutExtension(path) + "-" + DateTime.UtcNow.ToString(FileTimeStampFormat) + Path.GetExtension(path);
        }

        // Indicates whether a path is a relative path (not absolute)
        /// <summary>Checks whether a nonempty path is not rooted.</summary>
        public static bool IsRelativePath(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path, nameof(path));
            return !Path.IsPathRooted(path);
        }

        // Appends extension on to a file
        /// <summary>Appends the requested extension to the supplied path.</summary>
        public static string AppendExtension(string path, string extension)
        {
            ArgumentException.ThrowIfNullOrEmpty(path, nameof(path));
            ArgumentException.ThrowIfNullOrEmpty(extension, nameof(extension));

            if (!extension.StartsWith("."))
            {
                extension = "." + extension;
            }

            return path + extension;
        }

        // Appends standard temp file extension on to a file
        /// <summary>Creates a temporary filename using the requested naming context.</summary>
        public static string GetTemporaryFileName(string path)
        {
            return AppendExtension(path, TempFileExtension);
        }

        // Indicates whether a file is a container of other files - e.g. an indexable file archive
        /// <summary>Checks whether a path has a recognized container-file extension.</summary>
        public static bool IsContainer(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path, nameof(path));

            string extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && extension.Length > 1 && ContainerFileExtensions.Contains(extension.Substring(1));
        }

        /// <summary>Gets the application root directory used by file helpers.</summary>
        public static string ApplicationRootPath => Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory);

        /// <summary>Creates the requested subdirectory beneath the application root and returns its path.</summary>
        public static string CreateApplicationSubFolderPath(string subFolderName)
        {
            string directory = Path.Combine(Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory), subFolderName);
            Directory.CreateDirectory(directory);

            return directory;
        }

        // Returns the relative portion of an absolute path
        /// <summary>Computes a path relative to the supplied base location.</summary>
        public static string GetRelativePath(string absolutePath, string folder)
        {
            ArgumentException.ThrowIfNullOrEmpty(absolutePath, nameof(absolutePath));
            ArgumentException.ThrowIfNullOrEmpty(folder, nameof(folder));

            Uri pathUri = new Uri(absolutePath);

            // Folders must end in a slash
            if (!folder.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                folder += Path.DirectorySeparatorChar;
            }

            Uri folderUri = new Uri(folder);
            return Uri.UnescapeDataString(folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Attempts to extract all matching container entries and reports success.</summary>
        public static bool TryExtractAllContainerSubAddress(string containerFullAddress, string targetFolderPath, Action<Progress> progressCallback, out string error)
        {
            if (string.IsNullOrEmpty(containerFullAddress))
            {
                throw new ArgumentException($"{nameof(containerFullAddress)} cannot be null or empty", nameof(containerFullAddress));
            }

            if (string.IsNullOrEmpty(targetFolderPath))
            {
                throw new ArgumentException($"{nameof(targetFolderPath)} cannot be null or empty", nameof(targetFolderPath));
            }

            error = null;

            ZipFile zipFile = null;

            try
            {
                zipFile = new ZipFile(containerFullAddress);

                long totalBytes = 0;

                // Get the total bytes of all files in the container
                foreach (ZipEntry zipEntry in zipFile)
                {
                    if (zipEntry.IsFile)
                    {
                        totalBytes += zipEntry.Size;
                    }
                }

                progressCallback?.Invoke(new Progress()
                {
                    BytesProcessed = 0,
                    TotalBytes = totalBytes
                });

                long bytesProcessed = 0;

                // Throttle progress feedback to once per second to prevent flooding of progress events
                using (var progressFeedbackScope = new ProgressFeedbackScope(TimeSpan.FromSeconds(1), totalBytes, () =>
                {
                    progressCallback?.Invoke(new Progress()
                    {
                        BytesProcessed = Interlocked.Read(ref bytesProcessed),
                        TotalBytes = totalBytes
                    });
                }))
                {
                    // Now go through each file and extract
                    foreach (ZipEntry zipEntry in zipFile)
                    {
                        if (!zipEntry.IsFile)
                        {
                            continue;     // Ignore directories
                        }

                        string entryFileName = zipEntry.Name;

                        byte[] buffer = new byte[4096];     // 4K is optimum
                        Stream zipStream = zipFile.GetInputStream(zipEntry);

                        string fullZipToPath = Path.Combine(targetFolderPath, entryFileName);
                        string directoryName = Path.GetDirectoryName(fullZipToPath);

                        if (directoryName.Length > 0)
                        {
                            Directory.CreateDirectory(directoryName);
                        }

                        using (FileStream streamWriter = File.Create(fullZipToPath))
                        {
                            long startProgress = bytesProcessed;

                            StreamHelper.CopyWithProgress(zipStream, streamWriter, buffer, (p2) =>
                            {
                                Interlocked.Exchange(ref bytesProcessed, startProgress + p2.BytesProcessed);
                            });

                            progressCallback?.Invoke(new Progress()
                            {
                                BytesProcessed = bytesProcessed,
                                TotalBytes = totalBytes
                            });
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is ZipException || ex is IOException)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (zipFile != null)
                {
                    zipFile.IsStreamOwner = true;
                    zipFile.Close();
                }
            }

            return true;
        }

        /// <summary>Attempts to enumerate entry addresses within an asset container.</summary>
        public static bool TryGetAssetContainerSubAddresses(string containerFullAddress, out IEnumerable<ContainerFileInfo> containerFileInfos, out string error)
        {
            if (string.IsNullOrEmpty(containerFullAddress))
            {
                throw new ArgumentException($"{nameof(containerFullAddress)} cannot be null or empty", nameof(containerFullAddress));
            }

            containerFileInfos = null;
            error = null;

            if (!FileHelper.IsContainer(containerFullAddress))
            {
                throw new ArgumentException("Asset '" + containerFullAddress + "' is not a container", nameof(containerFullAddress));
            }

            // No error checking as this is performed IsContainer method
            string extension = Path.GetExtension(containerFullAddress);

            if (!string.IsNullOrEmpty(extension) && extension.Length > 1)
            {
                extension = extension.Substring(1);

                if (string.Equals(extension, ZipArchiveExtension, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        ZipFile zipFile = new ZipFile(containerFullAddress);
                        var fileInfos = new List<ContainerFileInfo>();

                        foreach (ZipEntry zipEntry in zipFile)
                        {
                            fileInfos.Add(new ContainerFileInfo() { SubAddress = zipEntry.Name, LastModified = zipEntry.DateTime });
                        }

                        return true;
                    }
                    catch (ZipException ex)
                    {
                        error = ex.Message;
                    }
                }
                else
                {
                    throw new InvalidOperationException("Unhandled container extension '" + extension + "'");
                }
            }

            return false;
        }

        /// <summary>Attempts to enumerate container entry addresses matching the supplied selection.</summary>
        public static bool TryGetAssetContainerMatchingSubAddresses(string containerFullAddress, string[] wildcards, out IEnumerable<ContainerFileInfo> containerFileInfos, out string error)
        {
            string selectionCriteria = string.Empty;

            if (wildcards != null && wildcards.Any())
            {
                List<string> wildcardsList = wildcards.ToList();

                StringBuilder sb = new StringBuilder();

                // Form the selection criteria for filenames using ZIP selection criteria syntax
                for (int index = 0; index < wildcardsList.Count; index++)
                {
                    if (!string.IsNullOrEmpty(wildcardsList[index]))
                    {
                        sb.AppendFormat(ZipNameCriteria, wildcardsList[index]);
                        if (index != (wildcardsList.Count - 1))
                        {
                            sb.Append(ZipOrOperator);
                        }
                    }
                }

                selectionCriteria = sb.ToString();
            }

            return TryGetAssetContainerMatchingSubAddresses(containerFullAddress, selectionCriteria, out containerFileInfos, out error);
        }

        /// <summary>Attempts to enumerate container entry addresses matching the supplied selection.</summary>
        public static bool TryGetAssetContainerMatchingSubAddresses(string containerFullAddress, string selectionCriteria, out IEnumerable<ContainerFileInfo> containerFileInfos, out string error)
        {
            containerFileInfos = null;
            error = null;

            if (!FileHelper.IsContainer(containerFullAddress))
            {
                throw new ArgumentException("Asset '" + containerFullAddress + "' is not a container", nameof(containerFullAddress));
            }

            // No error checking as this is performed IsContainer method
            string extension = Path.GetExtension(containerFullAddress);

            if (!string.IsNullOrEmpty(extension) && extension.Length > 1)
            {
                extension = extension.Substring(1);

                if (string.Equals(extension, ZipArchiveExtension, StringComparison.OrdinalIgnoreCase))
                {
                    ZipFile zipFile = null;

                    try
                    {
                        zipFile = new ZipFile(containerFullAddress);
                        List<ContainerFileInfo> matchingFileInfos = new List<ContainerFileInfo>();

                        foreach (ZipEntry zipEntry in zipFile)
                        {
                            // Note: Performance concerns
                            if (Wildcard.Match(selectionCriteria, zipEntry.Name))
                            {
                                matchingFileInfos.Add(new ContainerFileInfo()
                                {
                                    SubAddress = zipEntry.Name,
                                    LastModified = zipEntry.DateTime
                                });
                            }
                        }

                        return true;
                    }
                    catch (ZipException ex)
                    {
                        error = ex.Message;
                    }
                    finally
                    {
                        if (zipFile != null)
                        {
                            zipFile.IsStreamOwner = true;
                            zipFile.Close();
                        }
                    }
                }
                else
                {
                    throw new InvalidOperationException("Unhandled container extension '" + extension + "'");
                }
            }

            return false;
        }

        /// <summary>Attempts to open or extract the selected container asset.</summary>
        public static bool TryExtractAssetFromContainer(string containerFullAddress, string assetSubAddress, int chunkSize, Stream readStream, Stream writeStream, out string error)
        {
            error = null;

            bool isMatch = false;

            if (!FileHelper.IsContainer(containerFullAddress))
            {
                throw new ArgumentException("Asset '" + containerFullAddress + "' is not a container", nameof(containerFullAddress));
            }

            // No error checking as this is performed IsContainer method
            string extension = Path.GetExtension(containerFullAddress);

            if (!string.IsNullOrEmpty(extension) && extension.Length > 1)
            {
                extension = extension.Substring(1);

                if (string.Equals(extension, ZipArchiveExtension, StringComparison.OrdinalIgnoreCase))
                {
                    using (ZipInputStream inputStream = new ZipInputStream(readStream))
                    {
                        do
                        {
                            ZipEntry ze = inputStream.GetNextEntry();

                            if (ze == null)
                            {
                                break;
                            }

                            if (ze.IsDirectory)
                            {
                                continue;
                            }

                            if (string.Equals(ze.Name, assetSubAddress, StringComparison.OrdinalIgnoreCase))
                            {
                                int totalBytesRead = 0;
                                int bytesRead = 0;

                                do
                                {
                                    byte[] bytes = new byte[chunkSize];

                                    bytesRead = inputStream.Read(bytes, 0, chunkSize);
                                    if (bytesRead > 0)
                                    {
                                        writeStream.Write(bytes, 0, bytesRead);
                                    }

                                    totalBytesRead += bytesRead;
                                }
                                while (bytesRead > 0);

                                isMatch = true;

                                break;
                            }
                        }
                        while (true);
                    }
                }
                else
                {
                    throw new InvalidOperationException("Unhandled container extension '" + extension + "'");
                }
            }

            if (!isMatch)
            {
                error = "Unable to find asset '" + assetSubAddress + "' in asset container '" + containerFullAddress + "'.";
            }

            return isMatch;
        }

        /// <summary>Attempts to resolve and open an embedded assembly resource.</summary>
        public static bool TryGetEmbeddedResource(Assembly containingAssembly, string relativePath, out Stream stream)
        {
            ArgumentNullException.ThrowIfNull(containingAssembly, nameof(containingAssembly));
            ArgumentException.ThrowIfNullOrEmpty(relativePath, nameof(relativePath));

            stream = null;

            bool IsExpectedException(Exception ex)
            {
                return ex is ArgumentNullException || ex is ArgumentException || ex is FileLoadException || ex is FileNotFoundException || ex is BadImageFormatException;
            }

            try
            {
                stream = containingAssembly.GetManifestResourceStream(containingAssembly.GetName().Name + "." + relativePath.Replace('/', '.'));
            }
            catch (Exception ex) when (IsExpectedException(ex))
            {
                return false;
            }

            return true;
        }

        /// <summary>Attempts to resolve and open an embedded assembly resource.</summary>
        public static bool TryGetEmbeddedResource<TContent>(Assembly containingAssembly, string relativePath, Func<Stream, TContent> contentResolver, out TContent content)
        {
            content = default(TContent);

            if (TryGetEmbeddedResource(containingAssembly, relativePath, out Stream stream))
            {
                content = contentResolver(stream);
                return true;
            }

            return false;
        }

        /// <summary>Attempts to resolve and open an embedded assembly resource.</summary>
        public static bool TryGetEmbeddedResource(Assembly containingAssembly, string relativePath, out byte[] content)
        {
            return TryGetEmbeddedResource(containingAssembly, relativePath,
                (s) =>
                {
                    using (var memoryStream = new MemoryStream())
                    {
                        s.CopyTo(memoryStream);
                        return memoryStream.ToArray();
                    }
                },
                out content);
        }

        /// <summary>Attempts to resolve and open an embedded assembly resource.</summary>
        public static bool TryGetEmbeddedResource(Assembly containingAssembly, string relativePath, out string content)
        {
            return TryGetEmbeddedResource(containingAssembly, relativePath,
                (s) =>
                {
                    using (var reader = new StreamReader(s))
                    {
                        return reader.ReadToEnd();
                    }
                },
                out content);
        }

        /// <summary>Classifies anticipated filesystem failures that callers may handle as unavailable file access.</summary>
        public static bool IsExpectedFileException(Exception ex)
        {
            return ex is ArgumentException || ex is SecurityException || ex is UnauthorizedAccessException || ex is IOException || ex is DirectoryNotFoundException || ex is PathTooLongException || ex is ArgumentOutOfRangeException;
        }
    }
}
