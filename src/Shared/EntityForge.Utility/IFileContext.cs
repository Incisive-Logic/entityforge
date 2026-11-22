using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Shared.Repository
{
    /// <summary>Owns reusable file streams and coordinates file operations within a disposable context.</summary>
    public interface IFileContext : IDisposable
    {
        /// <summary>Gets the context-managed stream for the specified path.</summary>
        Stream GetFileStream(string path);

        /// <summary>Opens a context-managed stream with explicit file mode, access and sharing options.</summary>
        Stream CreateFileStream(string path, FileMode fileMode, FileAccess fileAccess, FileShare fileShare);

        /// <summary>Flushes all streams managed by this context.</summary>
        void FlushAll();

        /// <summary>Flushes the managed stream for the specified path.</summary>
        void Flush(string path);

        /// <summary>Deletes the specified file through the context.</summary>
        void Delete(string path);

        /// <summary>Moves a file with an explicit overwrite policy.</summary>
        void Move(string sourcePath, string destinationPath, bool overwrite);

        /// <summary>Closes and releases the managed stream for the specified path.</summary>
        void Close(string path);
    }
}
