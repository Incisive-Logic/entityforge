using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    /// <summary>Writes program information and severity-styled console messages.</summary>
    public static class ConsoleHelper
    {
        private static void WriteMessage(string message, ConsoleColor color)
        {
            try
            {
                Console.ForegroundColor = color;
            }
            catch (PlatformNotSupportedException)
            {
                // Ignore
            }

            Console.WriteLine($"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")} {message}");

            try
            {
                Console.ResetColor();
            }
            catch (PlatformNotSupportedException)
            {
                // Ignore
            }
        }

        /// <summary>Writes the supplied program identity information to the console.</summary>
        public static void WriteProgramInformation(string programName)
        {
            Console.WriteLine($"Entity Forge {programName}");
            Console.WriteLine($"(c) Copyright Incisive Logic {DateTime.UtcNow.Year}");
            Console.WriteLine();
        }

        /// <summary>Writes an error message using the helper's error styling.</summary>
        public static void WriteError(string message)
        {
            WriteMessage(message, ConsoleColor.Red);
        }

        /// <summary>Writes a warning message using the helper's warning styling.</summary>
        public static void WriteWarning(string message)
        {
            WriteMessage(message, ConsoleColor.Yellow);
        }

        /// <summary>Writes an informational console message.</summary>
        public static void WriteInfo(string message)
        {
            WriteMessage(message, ConsoleColor.White);
        }

        /// <summary>Writes a success message using the helper's success styling.</summary>
        public static void WriteSuccess(string message)
        {
            WriteMessage(message, ConsoleColor.Green);
        }
    }
}
