using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace EntityForge.Db.Storage.Pages;

/// <summary>A bounded, process-wide diagnostic role registered at composition time.</summary>
public sealed class AppendMergeLookupRole
{
    private const int MaximumRoles = 32;
    private const int MaximumNameLength = 64;
    private static readonly Dictionary<string, AppendMergeLookupRole> Roles = new(StringComparer.Ordinal);

    internal string Name
    {
        get;
    }

    private AppendMergeLookupRole(string name) => Name = name;

    /// <summary>Returns the process-wide diagnostic role with this exact name, registering it if needed.</summary>
    /// <param name="name">One to 64 ASCII letters, digits or underscores; compared case-sensitively.</param>
    /// <returns>The shared role instance for the name.</returns>
    /// <exception cref="ArgumentException">The name is null, blank, too long or contains unsupported characters.</exception>
    /// <exception cref="InvalidOperationException">All 32 distinct process-wide roles have already been registered.</exception>
    /// <remarks>Register a fixed vocabulary at composition time; registrations are not removed on scope disposal.</remarks>
    public static AppendMergeLookupRole Register(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > MaximumNameLength || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
        {
            throw new ArgumentException("Invalid diagnostic role name.", nameof(name));
        }

        lock (Roles)
        {
            if (Roles.TryGetValue(name, out var role))
            {
                return role;
            }

            if (Roles.Count == MaximumRoles)
            {
                throw new InvalidOperationException("Diagnostic role limit reached.");
            }

            role = new(name);
            Roles.Add(name, role);
            return role;
        }
    }
}

internal enum LookupWork
{
    RequestedKeys, RunProbes, CandidateKeys, DenseRuns, SparseRuns,
    BoundaryHits, BoundaryMisses, BoundaryRecords, LookupPages, VisitedRecords, DecodedRecords,
    ValidatedUnmatchedRecords, DeviceReads, DeviceBytes, BufferReads, BufferBytes,
    CacheHits, CacheMisses, CacheEvictions, Scopes, IncompleteScopes, SequentialRuns
}

/// <summary>Optional scoped work counts; never retains keys, values or page identities.</summary>
public static class AppendMergeLookupTelemetry
{
    private static readonly Meter Meter = new(PhysicalPageTelemetry.MeterName);
    private static readonly Histogram<long> Quantity = Meter.CreateHistogram<long>(
        "entityforge.db.lookup.work", "value");

    private static readonly string[] Names = Enum.GetNames<LookupWork>();
    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Returns null without allocating or changing context when no diagnostic consumer listens.</summary>
    public static Scope? Begin(AppendMergeLookupRole role, int requestedKeys)
    {
        if (!Quantity.Enabled)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(role);
        string stage = role.Name;
        var scope = new Scope(stage, Current.Value);
        scope.Add(LookupWork.RequestedKeys, requestedKeys);
        Current.Value = scope;
        return scope;
    }

    internal static void Add(LookupWork work, long value = 1) => Current.Value?.Add(work, value);

    /// <summary>An independent asynchronous lookup scope, including partial work when lookup fails.</summary>
    public sealed class Scope : IDisposable
    {
        private readonly string _stage;
        private readonly Scope? _parent;
        private readonly long[] _values = new long[Names.Length];
        private int _disposed;
        private bool _complete;

        internal Scope(string stage, Scope? parent)
        {
            _stage = stage;
            _parent = parent;
        }

        internal void Add(LookupWork work, long value) => Interlocked.Add(ref _values[(int)work], value);

        /// <summary>Marks this lookup complete after its awaited reader returned successfully.</summary>
        public void Complete() => _complete = true;

        /// <summary>Emits the scope's work counts once and restores its parent when this is the current scope.</summary>
        /// <remarks>Disposing without calling <see cref="Complete"/> records an incomplete scope and retains partial work counts.</remarks>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (ReferenceEquals(Current.Value, this))
            {
                Current.Value = _parent;
            }

            Add(LookupWork.Scopes, 1);
            if (!_complete)
            {
                Add(LookupWork.IncompleteScopes, 1);
            }

            for (int index = 0; index < _values.Length; index++)
            {
                TagList tags = new()
                {
                    { "entityforge.db.stage", _stage },
                    { "entityforge.db.lookup.work", Names[index] }
                };
                Quantity.Record(Interlocked.Read(ref _values[index]), tags);
            }
        }
    }
}
