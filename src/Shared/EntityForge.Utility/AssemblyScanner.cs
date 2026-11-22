using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading.Tasks;

namespace EntityForge.Utility
{
    #region Supporting Classes

    /// <summary>Reports that an assembly scan could not access its requested directory.</summary>
    public class AssemblyScannerException : Exception
    {
        /// <summary>Creates an assembly-scan failure with the supplied description.</summary>
        public AssemblyScannerException(string message) : base(message)
        {
        }
    }

    #endregion

    /// <summary>Loads assemblies from a directory and discovers concrete classes in loaded assemblies.</summary>
    public static class AssemblyScanner
    {
        private static readonly HashSet<string> s_assemblyExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".dll",
            ".exe"
        };

        /// <summary>Attempts to load matching DLL and EXE files, optionally recursively; individual unloadable files are skipped.</summary>
        public static void ScanDirectory(string path, bool recursive, string fileFilter = "*")
        {
            if (!Directory.Exists(path))
            {
                throw new AssemblyScannerException($"Cannot find folder '{path}'");
            }

            IEnumerable<string> files = null;

            try
            {
                files = Directory.EnumerateFiles(path, fileFilter, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Where(p => s_assemblyExtensions.Contains(Path.GetExtension(p))).ToList();
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is SecurityException)
            {
                throw new AssemblyScannerException($"Cannot browse files in folder '{path}' ('{ex.Message}')");
            }

            foreach (string file in files)
            {
                try
                {
                    Assembly.LoadFile(file);
                }
                catch
                {
                    // Ignore and continue. Errors may include file has been removed, or not a .NET assembly
                }
            }
        }

        /// <summary>Returns loaded, nonabstract classes carrying the requested attribute, optionally restricted to an assignable interface.</summary>
        public static IEnumerable<Type> GetClassTypesWithAttribute<TInterface, TAttribute>()
            where TAttribute : Attribute
        {
            return AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes())
                .Where(t => typeof(TInterface).IsAssignableFrom(t) && t.IsClass && !t.IsAbstract && t.GetCustomAttribute<TAttribute>() != null).ToList();
        }

        /// <summary>Returns loaded, nonabstract classes carrying the requested attribute, optionally restricted to an assignable interface.</summary>
        public static IEnumerable<Type> GetClassTypesWithAttribute<TAttribute>()
            where TAttribute : Attribute
        {
            return AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => a.GetTypes())
                .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<TAttribute>() != null).ToList();

        }
    }
}
