using EntityForge.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Shared.Repository
{
    #region Supporting Classes

    /// <summary>Reports an error while accessing files through a managed file context.</summary>
    public class FileContextException : Exception
    {
        /// <summary>Creates a file-context failure with its description and optional underlying cause.</summary>
        public FileContextException(string message) : base(message)
        {
        }
    }

    #endregion

    /// <summary>
    /// Manages file stream(s) for file based repositories
    /// Also abstracts out the file stream for unit testability
    /// </summary>
    public class FileContext : IFileContext
    {
        private readonly Dictionary<string, FileStream> _files = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public Stream GetFileStream(string path)
        {
            lock (_files)
            {
                return _files[path];
            }
        }

        /// <inheritdoc />
        public Stream CreateFileStream(string path, FileMode fileMode, FileAccess fileAccess, FileShare fileShare)
        {
            lock (_files)
            {
                try
                {
                    FileStream fileStream = null;

                    if (!_files.ContainsKey(path))
                    {
                        fileStream = new FileStream(path, fileMode, fileAccess, fileShare);
                        _files.Add(path, fileStream);
                    }

                    return fileStream;
                }
                catch (Exception ex) when (FileHelper.IsExpectedFileException(ex))
                {
                    throw new FileContextException(ex.Message);
                }
            }
        }

        /// <inheritdoc />
        public void Delete(string path)
        {
            lock (_files)
            {
                bool inContext = _files.TryGetValue(path, out FileStream fileStream);

                try
                {
                    if (inContext)
                    {
                        fileStream.Flush();
                        fileStream.Dispose();
                    }

                    File.Delete(path);

                    if (inContext)
                    {
                        _files.Remove(path);
                    }
                }
                catch (Exception ex) when (FileHelper.IsExpectedFileException(ex))
                {
                    throw new FileContextException(ex.Message);
                }
            }
        }

        /// <inheritdoc />
        public void Move(string sourcePath, string destinationPath, bool overwrite)
        {
            lock (_files)
            {
                if (_files.TryGetValue(sourcePath, out FileStream fileStream))
                {
                    fileStream.Close();
                    _files.Remove(sourcePath);
                }

                try
                {
                    if (overwrite && File.Exists(destinationPath))
                    {
                        File.Delete(destinationPath);
                    }

                    File.Move(sourcePath, destinationPath);
                }
                catch (Exception ex) when (FileHelper.IsExpectedFileException(ex))
                {
                    throw new FileContextException(ex.Message);
                }
            }
        }

        /// <inheritdoc />
        public void Flush(string path)
        {
            lock (_files)
            {
                try
                {
                    if (_files.TryGetValue(path, out FileStream fileStream))
                    {
                        fileStream.Flush();
                    }
                }
                catch (Exception ex) when (FileHelper.IsExpectedFileException(ex))
                {
                    throw new FileContextException($"Cannot flush file '{path}' ('{ex.Message}')");
                }
            }
        }

        /// <inheritdoc />
        public void FlushAll()
        {
            lock (_files)
            {
                try
                {
                    foreach (var file in _files.Values)
                    {
                        file.Flush();
                    }
                }
                catch (Exception ex) when (FileHelper.IsExpectedFileException(ex))
                {
                    throw new FileContextException($"Cannot flush files ('{ex.Message}')");
                }
            }
        }

        /// <inheritdoc />
        public void Close(string path)
        {
            lock (_files)
            {
                if (_files.TryGetValue(path, out FileStream fileStream))
                {
                    try
                    {
                        // Ensure flushed
                        fileStream.Flush();
                        fileStream.Dispose();
                    }
                    catch (Exception ex) when (FileHelper.IsExpectedFileException(ex))
                    {
                        throw new FileContextException($"Cannot close file '{path}' ('{ex.Message}')");
                    }
                }
            }
        }

        /// <summary>Closes streams owned by the file context.</summary>
        public void Dispose()
        {
            lock (_files)
            {
                FlushAll();

                foreach (var fileStream in _files.Values)
                {
                    fileStream.Dispose();
                }
            }
        }
    }
}
