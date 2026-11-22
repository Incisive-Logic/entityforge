namespace EntityForge.Db;

// Exception.Data keeps diagnostics available without changing the primary exception contract.
internal static class EntityFailureDiagnostics
{
    internal const string CleanupFailures = "EntityForge.Db.CleanupFailures";

    internal static void Attach(Exception primary, Exception secondary)
    {
        try
        {
            var failures = primary.Data[CleanupFailures] as Exception[] ?? [];
            if (!failures.Any(item => ReferenceEquals(item, secondary)))
            {
                primary.Data[CleanupFailures] = failures.Append(secondary).ToArray();
            }
        }
        catch
        {
            /* Diagnostics must never interrupt cleanup or replace the primary failure. */
        }
    }
}
