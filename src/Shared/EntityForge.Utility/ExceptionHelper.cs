using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Unwraps aggregate failures while preserving useful exception propagation.</summary>
    public static class AggregateExceptionHelper
    {
        /// <summary>Unwraps the aggregate exception and rethrows the selected underlying failure.</summary>
        public static void UnwrapAndRethrow(this AggregateException ex)
        {
            if (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }

            throw ex;
        }
    }

    /// <summary>
    /// A centralized repository of standard error messages
    /// Messages are in present tense
    /// </summary>
    public static class ExceptionHelper
    {
        private const string DetailPostfix = " ('{0}')";

        private static string WithDetail(string message, string detail)
        {
            return !string.IsNullOrEmpty(detail) ? (message + string.Format(DetailPostfix, detail)) : string.Empty;
        }

        private const string UnauthorisedAccessExceptionMessage = "Cannot access '{0}' due to insufficient permissions";
        private const string InvalidPathExceptionMessage = "The path '{0}' is invalid";
        private const string FileSystemExceptionMessage = "A file system error occurred accessing path '{0}'";
        private const string DirectoryNotFoundExceptionMessage = "Cannot find directory '{0}'";
        private const string CannotMoveFileExceptionMessage = "Cannot move file '{0}' to '{1}' ('{2}')";
        private const string CannotAccessExceptionMessage = "Cannot access '{0}'";

        /// <summary>Creates a contextual exception describing denied access.</summary>
        public static string UnauthorizedAccess(string path, string detail = null)
        {
            return WithDetail(string.Format(UnauthorisedAccessExceptionMessage, path), detail);
        }

        /// <summary>Creates a contextual exception describing an invalid filesystem path.</summary>
        public static string InvalidPath(string path, string detail = null)
        {
            return WithDetail(string.Format(InvalidPathExceptionMessage, path), detail);
        }

        /// <summary>Creates a contextual exception for a filesystem operation failure.</summary>
        public static string FileSystemError(string path, string detail = null)
        {
            return WithDetail(string.Format(FileSystemExceptionMessage, path), detail);
        }

        /// <summary>Creates a contextual exception identifying a missing directory.</summary>
        public static string DirectoryNotFound(string path, string detail = null)
        {
            return WithDetail(DirectoryNotFoundExceptionMessage, detail);
        }

        /// <summary>Creates a contextual exception describing a failed file move.</summary>
        public static string CannotMoveFile(string sourcePath, string destinationPath, string error)
        {
            return string.Format(CannotMoveFileExceptionMessage, sourcePath, destinationPath, error);
        }

        /// <summary>Creates a contextual exception describing an inaccessible resource.</summary>
        public static string CannotAccess(string path, string detail = null)
        {
            return WithDetail(string.Format(CannotAccessExceptionMessage, path), detail);
        }

        /// <summary>
        /// Causes a crash by ensuring an exception is thrown on a thread pool thread that
        /// does not swallow unhandled exceptions
        /// </summary>
        /// <param name="ex">The exception you wish to use to crash the process</param>
        public static void Crash(Exception ex)
        {
            var dispatchInfo = ExceptionDispatchInfo.Capture(ex);

            ThreadPool.QueueUserWorkItem(s =>
            {
                dispatchInfo.Throw();
            });
        }
    }
}
