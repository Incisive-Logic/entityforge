namespace EntityForge.Db.Storage.Pages;

/// <summary>Attributes physical page activity to a bounded, profile-neutral workload class.</summary>
public static class PhysicalPageWorkload
{
    private const string ForegroundWrite = "foreground-write";
    private const string Maintenance = "maintenance";

    /// <summary>Attributes physical page activity in the returned scope to a foreground write.</summary>
    public static IDisposable EnterForegroundWrite() => PhysicalPageTelemetryContext.Enter(ForegroundWrite);

    /// <summary>Attributes physical page activity in the returned scope to one maintenance target.</summary>
    public static IDisposable EnterMaintenance(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return PhysicalPageTelemetryContext.Enter(Maintenance, target);
    }
}

/// <summary>Propagates low-cardinality work attribution through asynchronous physical-page operations.</summary>
internal static class PhysicalPageTelemetryContext
{
    private static readonly AsyncLocal<Scope?> CurrentScope = new();

    public static string Workload => CurrentScope.Value?.Workload ?? "foreground-read";

    public static string? Target => CurrentScope.Value?.Target;

    public static bool IsMaintenance => string.Equals(
        CurrentScope.Value?.Workload, "maintenance", StringComparison.Ordinal);

    public static IDisposable Enter(string workload, string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workload);
        Scope? parent = CurrentScope.Value;
        var scope = new Scope(workload, target, parent);
        CurrentScope.Value = scope;
        return scope;
    }

    private sealed class Scope(string workload, string? target, Scope? parent) : IDisposable
    {
        public string Workload { get; } = workload;

        public string? Target { get; } = target;

        public void Dispose()
        {
            if (ReferenceEquals(CurrentScope.Value, this))
            {
                CurrentScope.Value = parent;
            }
        }
    }
}
