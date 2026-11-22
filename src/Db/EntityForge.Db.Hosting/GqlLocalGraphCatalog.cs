using EntityForge.Db.Gql;
using System.Collections.ObjectModel;

namespace EntityForge.Db.Hosting;

/// <summary>Maps GQL graph names to independently owned local property-graph stores.</summary>
/// <remarks>Single names are decoded identifiers; qualified names use dotted GQL reference syntax. Both are case-sensitive.
/// All stores share the host's security and storage policy.
/// Requests cannot supply filesystem paths. This catalog does not create graph types or a distributed routing table.</remarks>
public sealed class GqlLocalGraphCatalog
{
    /// <summary>Creates an immutable catalog with a name for the host's default graph and up to sixteen additional stores.</summary>
    /// <param name="defaultGraphName">The name assigned to the host's existing local store.</param>
    /// <param name="graphDirectories">Additional graph names and their exclusive data directories; relative paths resolve now.</param>
    /// <exception cref="ArgumentException">Names are empty, duplicated, too long, or directories overlap.</exception>
    public GqlLocalGraphCatalog(string defaultGraphName, IReadOnlyDictionary<string, string> graphDirectories)
        : this(defaultGraphName, graphDirectories, new Dictionary<string, string>())
    {
    }

    /// <summary>Creates a catalog containing both single graph names and object-qualified graph references.</summary>
    /// <param name="defaultGraphName">The decoded identifier assigned to the host's default local store.</param>
    /// <param name="graphDirectories">Additional decoded single identifiers and their exclusive data directories.</param>
    /// <param name="qualifiedGraphDirectories">Dotted GQL graph references, such as sales.orders, and their exclusive directories. Identifier components may be delimited.</param>
    /// <exception cref="ArgumentException">References are invalid, identities duplicate, more than sixteen stores are supplied, or directories overlap.</exception>
    public GqlLocalGraphCatalog(string defaultGraphName, IReadOnlyDictionary<string, string> graphDirectories,
        IReadOnlyDictionary<string, string> qualifiedGraphDirectories)
    {
        ValidateName(defaultGraphName);
        ArgumentNullException.ThrowIfNull(graphDirectories);
        ArgumentNullException.ThrowIfNull(qualifiedGraphDirectories);
        if (graphDirectories.Count > 16 || qualifiedGraphDirectories.Count > 16 - graphDirectories.Count)
        {
            throw new ArgumentException("A local GQL catalog supports at most sixteen additional graphs.", nameof(graphDirectories));
        }
        var directories = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, directory) in graphDirectories)
        {
            ValidateName(name);
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            if (String.Equals(name, defaultGraphName, StringComparison.Ordinal) || !directories.TryAdd(name, Path.GetFullPath(directory)))
            {
                throw new ArgumentException("Every catalog graph name must be distinct.", nameof(graphDirectories));
            }
        }
        var qualified = new Dictionary<string, string>(StringComparer.Ordinal);
        var stores = new Dictionary<string, string>(directories, StringComparer.Ordinal);
        foreach (var (reference, directory) in qualifiedGraphDirectories)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directory);
            string key = GraphGqlStandardSyntax.QualifiedLocalGraphKey(reference);
            string path = Path.GetFullPath(directory);
            if (!stores.TryAdd(key, path))
            {
                throw new ArgumentException("Every qualified graph identity must be distinct.", nameof(qualifiedGraphDirectories));
            }
            qualified.Add(reference, path);
        }
        DefaultGraphName = defaultGraphName;
        GraphDirectories = new ReadOnlyDictionary<string, string>(directories);
        QualifiedGraphDirectories = new ReadOnlyDictionary<string, string>(qualified);
        StoreDirectories = new ReadOnlyDictionary<string, string>(stores);
        ValidateDirectories(stores.Values);
    }

    /// <summary>Gets the name of the default local graph, also selected by HOME_GRAPH.</summary>
    public string DefaultGraphName
    {
        get;
    }

    /// <summary>Gets additional graph names and their resolved absolute data directories.</summary>
    public IReadOnlyDictionary<string, string> GraphDirectories
    {
        get;
    }

    /// <summary>Gets configured object-qualified GQL references and their resolved absolute data directories.</summary>
    public IReadOnlyDictionary<string, string> QualifiedGraphDirectories
    {
        get;
    }

    internal IReadOnlyDictionary<string, string> StoreDirectories
    {
        get;
    }

    internal void ValidateDefaultDirectory(string directory) => ValidateDirectories(StoreDirectories.Values.Append(Path.GetFullPath(directory)));

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 1024 || name.Contains('\0'))
        {
            throw new ArgumentException("A graph name must contain at most 1024 characters and no NUL.", nameof(name));
        }
    }

    internal static void ValidateDirectories(IEnumerable<string> directories)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var paths = directories.Select(path => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar).ToArray();
        for (int index = 0; index < paths.Length; index++)
        {
            for (int other = 0; other < index; other++)
            {
                if (paths[index].StartsWith(paths[other], comparison) || paths[other].StartsWith(paths[index], comparison))
                {
                    throw new ArgumentException("Catalog stores must use distinct, non-overlapping data directories.");
                }
            }
        }
    }
}
