using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>
    /// Simple offline file based logging for diagnostics purposes
    /// </summary>
    public class SimpleLog
    {
        private readonly string _logFilePath;
        private const string LogFileNameFormat = "{0}.log";

        /// <summary>Creates a simple text logger using the supplied output configuration.</summary>
        public SimpleLog(string logName, string logFolderName)
        {
            ArgumentException.ThrowIfNullOrEmpty(logName, nameof(logName));
            ArgumentException.ThrowIfNullOrEmpty(logFolderName, nameof(logFolderName));

            if (IsEnabled)
            {
                // Form the default path name and create deep log folder if it does not exist already
                string directory = FileHelper.CreateApplicationSubFolderPath(logFolderName);

                _logFilePath = Path.Combine(directory, string.Format(LogFileNameFormat, logName));

                try
                {
                    if (File.Exists(_logFilePath))
                    {
                        File.Delete(_logFilePath);
                    }
                }
                catch (Exception ex)
                {
                    // We don't allow to take down the pipeline
                    Debug.WriteLine($"Couldn't start deep logging to due an error {ex.Message}.");
                }
            }
        }

        /// <summary>Gets or sets whether log output is enabled.</summary>
        public static bool IsEnabled
        {
            get; set;
        }

        // Note: This is very inelegant, but it ensures a completely independent log and keeps attempting to write
        // until it succeeds
        /// <summary>Writes a log line when logging is enabled.</summary>
        public void WriteLine(string message)
        {
            if (!IsEnabled)
            {
                return;
            }

            bool written = false;
            while (!written)
            {
                try
                {
                    using (var fileWriter = File.AppendText(_logFilePath))
                    {
                        fileWriter.WriteLine($"{DateTime.Now}: {message}");
                        fileWriter.Flush();
                    }

                    written = true;
                }
                catch
                {
                    // Deliberately prevent any error stopping the process
                }
            }
        }
    }
}
