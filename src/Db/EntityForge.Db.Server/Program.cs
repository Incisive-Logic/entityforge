using EntityForge.Db.Hosting;
using EntityForge.Db;
using EntityForge.Db.Storage.Local;
using EntityForge.Db.Storage;
using EntityForge.Db.Server;

if (args.Length > 0 && args[0] == "demo")
{
    await SemanticDemo.RunAsync(args[1..]);
    return;
}

const string ProfileEnvironmentVariable = "ENTITYFORGE_DB_PROFILE";

const string PropertyGraphProfileName = "property-graph";

const string RdfProfileName = "rdf";

const string SemanticProfileName = "semantic";

const string AdministratorTokenEnvironmentVariable = "ENTITYFORGE_DB_ADMIN_TOKEN";

const string AdministratorTokenFileEnvironmentVariable = "ENTITYFORGE_DB_ADMIN_TOKEN_FILE";

const string WriterTokenEnvironmentVariable = "ENTITYFORGE_DB_WRITER_TOKEN";

const string WriterTokenFileEnvironmentVariable = "ENTITYFORGE_DB_WRITER_TOKEN_FILE";

const string ReaderTokenEnvironmentVariable = "ENTITYFORGE_DB_READER_TOKEN";

const string ReaderTokenFileEnvironmentVariable = "ENTITYFORGE_DB_READER_TOKEN_FILE";

const string RequireHttpsEnvironmentVariable = "ENTITYFORGE_DB_REQUIRE_HTTPS";

const string MaximumConcurrentRequestsEnvironmentVariable = "ENTITYFORGE_DB_MAX_CONCURRENT_REQUESTS";

const string StudioAssetsEnvironmentVariable = "ENTITYFORGE_DB_STUDIO_ASSETS";

string dataDirectory = Environment.GetEnvironmentVariable("ENTITYFORGE_DB_DATA_DIRECTORY") ??
    EntityForgeDbHostOptions.DefaultDataDirectory;

if (args.Length > 0 && new[] { "backup", "verify-backup", "restore", "backup-gql-host", "restore-gql-host", "upgrade-gql-catalog" }.Contains(args[0], StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var backups = new LocalDatabaseBackupManager();
        if (string.Equals(args[0], "upgrade-gql-catalog", StringComparison.OrdinalIgnoreCase))
        {
            bool expireReceipts = args.Length == 3 && string.Equals(args[2], "--expire-receipts", StringComparison.Ordinal);
            RequireArgumentCount(args, expireReceipts ? 3 : 2, "upgrade-gql-catalog <managed-catalog-directory> [--expire-receipts]");
            bool upgraded = await EntityForgeDbRuntime.UpgradeManagedGqlCatalogAsync(args[1], expireReceipts);
            Console.WriteLine(upgraded ? "Upgraded managed GQL catalog to format 4; original metadata retained as catalog.format3.json." : "Managed GQL catalog is already format 4.");
            if (upgraded && expireReceipts)
            {
                Console.WriteLine("Prior catalog receipts expired. Do not replay prior operations under the new generation.");
            }
            return;
        }
        if (string.Equals(args[0], "restore-gql-host", StringComparison.OrdinalIgnoreCase))
        {
            bool expireCatalogReceipts = args.Length == 4 && string.Equals(args[3], "--expire-catalog-receipts", StringComparison.Ordinal);
            RequireArgumentCount(args, expireCatalogReceipts ? 4 : 3, "restore-gql-host <backup-directory> <new-target-directory> [--expire-catalog-receipts]");
            await EntityForgeDbRuntime.RestoreGqlHostBackupAsync(args[1], args[2], new EntityForgeDbHostOptions(), expireCatalogReceipts: expireCatalogReceipts);
            Console.WriteLine("Restored GQL host. Set ENTITYFORGE_DB_PROFILE=property-graph and ENTITYFORGE_DB_GQL_RECOVERY_DIRECTORY=" + Path.GetFullPath(args[2]));
            if (expireCatalogReceipts)
            {
                Console.WriteLine("Prior catalog receipts expired in the restored target. Do not replay prior operations under its new generation.");
            }
            return;
        }
        if (string.Equals(args[0], "backup-gql-host", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(args, 2, "backup-gql-host <new-backup-directory>");
            var backupBuilder = EntityForgeDbServer.CreateBuilder().UseLocalStorage(dataDirectory);
            ConfigureProfile(backupBuilder, Environment.GetEnvironmentVariable(ProfileEnvironmentVariable) ?? SemanticProfileName);
            await ConfigureGqlStorageAsync(backupBuilder, dataDirectory);
            await using var backupHost = backupBuilder.Build();
            var options = backupHost.Application.Services.GetRequiredService<EntityForgeDbHostOptions>();
            if (options.ManagedGqlDirectory is null || !File.Exists(Path.Combine(options.ManagedGqlDirectory, "catalog.json")) ||
                options.GqlGraphs!.StoreDirectories.Values.Append(options.DataDirectory).Any(path => !File.Exists(Path.Combine(path, options.ManifestFileName))))
            {
                throw new InvalidOperationException("GQL host backup requires an existing managed catalog and every configured database manifest.");
            }
            var runtime = backupHost.Application.Services.GetRequiredService<EntityForgeDbRuntime>();
            await runtime.StartAsync(CancellationToken.None);
            await runtime.CreateGqlHostBackupAsync(args[1]);
            Console.WriteLine("Backed up all GQL graph stores to " + Path.GetFullPath(args[1]));
            return;
        }
        if (string.Equals(args[0], "verify-backup", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(args, 2, "verify-backup <backup-path>");
            LocalDatabaseBackupInfo info = await backups.VerifyAsync(args[1]);
            Console.WriteLine($"Verified {info.CheckpointBytes} checkpoint bytes ({info.Sha256}).");
            return;
        }
        if (string.Equals(args[0], "restore", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(args, 3, "restore <backup-path> <empty-target-directory>");
            LocalDatabaseBackupInfo info = await backups.RestoreAsync(args[1], args[2],
                EntityForgeDbHostOptions.DefaultCheckpointFileName,
                EntityForgeDbHostOptions.DefaultCommitLogFileName,
                EntityForgeDbHostOptions.DefaultManifestFileName,
                EntityForgeDbHostOptions.DefaultChangeJournalFileName,
                EntityForgeDbHostOptions.DefaultPageFileName, new EntityForge.Db.Storage.Pages.GroupedRedoRestoreLogInitializer());
            Console.WriteLine($"Restored {info.CheckpointBytes} checkpoint and {info.ChangeJournalBytes} change-journal bytes ({info.Sha256}).");
            return;
        }
        if (string.Equals(args[0], "backup", StringComparison.OrdinalIgnoreCase))
        {
            RequireArgumentCount(args, 2, "backup <backup-path>");
            if (!File.Exists(Path.Combine(dataDirectory, EntityForgeDbHostOptions.DefaultManifestFileName)))
            {
                throw new InvalidOperationException("Backup requires an existing EntityForge database directory with a format manifest.");
            }
            EntityForgeDbHostBuilder backupBuilder = EntityForgeDbServer.CreateBuilder()
                .UseLocalStorage(dataDirectory);
            ConfigureProfile(backupBuilder, Environment.GetEnvironmentVariable(ProfileEnvironmentVariable) ?? SemanticProfileName);
            await using EntityForgeDbHost backupHost = backupBuilder.Build();
            EntityForgeDbRuntime runtime = backupHost.Application.Services
                .GetRequiredService<EntityForgeDbRuntime>();
            // Offline administration needs storage ownership and checkpointing,
            // not Kestrel or any HTTP endpoints. Host disposal owns the runtime.
            await runtime.StartAsync(CancellationToken.None);
            LocalDatabaseBackupInfo info = await runtime.CreateBackupAsync(args[1]);
            Console.WriteLine($"Backed up {info.CheckpointBytes} checkpoint bytes ({info.Sha256}).");
            return;
        }
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("Command failed: " + exception.Message);
        Environment.ExitCode = 1;
    }
    return;
}

EntityForgeDbHostBuilder builder = EntityForgeDbServer.CreateBuilder(args)
    .UseLocalStorage(dataDirectory);

try
{
    builder.UseChangeJournalRetention(new(
        ReadHistorySetting("MAX_BYTES", DatabaseChangeJournalRetentionOptions.DefaultMaximumRetainedBytes, 1, Int64.MaxValue),
        (int)ReadHistorySetting("MIN_CHANGES", DatabaseChangeJournalRetentionOptions.DefaultMinimumRetainedChangeCount, 0, Int32.MaxValue),
        TimeSpan.FromTicks(ReadHistorySetting("MIN_SECONDS", (long)DatabaseChangeJournalRetentionOptions.DefaultMinimumRetainedDuration.TotalSeconds,
            0, Int64.MaxValue / TimeSpan.TicksPerSecond) * TimeSpan.TicksPerSecond)));
}
catch (ArgumentException failure)
{
    Console.Error.WriteLine(failure.Message);
    Environment.ExitCode = 1;
    return;
}

static long ReadHistorySetting(string suffix, long fallback, long minimum, long maximum)
{
    string name = "ENTITYFORGE_DB_CHANGE_JOURNAL_" + suffix;
    string? text = Environment.GetEnvironmentVariable(name);
    if (text is null)
    {
        return fallback;
    }
    if (!Int64.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long value) ||
        value < minimum || value > maximum)
    {
        throw new ArgumentException($"{name} must be an integer between {minimum} and {maximum}.");
    }
    return value;
}

builder.UseQueryScans(new()
{
    MaximumActiveScans = ReadScanSetting("MAX_ACTIVE", 8),
    MaximumRows = ReadScanSetting("MAX_ROWS", 1_000_000),
    MaximumExaminedElements = ReadScanSetting("MAX_EXAMINED", 10_000_000),
    PageSize = ReadScanSetting("PAGE_SIZE", 256),
    MaximumSortTemporaryBytes = ReadScanSetting("SORT_TEMP_BYTES", 0),
    Timeout = TimeSpan.FromSeconds(ReadScanSetting("TIMEOUT_SECONDS", 300))
});

static int ReadScanSetting(string suffix, int fallback) => Int32.Parse(
    Environment.GetEnvironmentVariable("ENTITYFORGE_DB_SCAN_" + suffix) ??
    fallback.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);

string? studioAssets = Environment.GetEnvironmentVariable(StudioAssetsEnvironmentVariable);

string bundledStudioAssets = Path.Combine(AppContext.BaseDirectory, "studio");

if (String.IsNullOrWhiteSpace(studioAssets) && Directory.Exists(bundledStudioAssets))
{
    studioAssets = bundledStudioAssets;
}

if (!String.IsNullOrWhiteSpace(studioAssets))
{
    builder.UseStudio(studioAssets);
}

bool requireHttps = Boolean.Parse(Environment.GetEnvironmentVariable(RequireHttpsEnvironmentVariable) ?? "true");

int maximumConcurrentRequests = Int32.Parse(Environment.GetEnvironmentVariable(
    MaximumConcurrentRequestsEnvironmentVariable) ??
    EntityForgeDbBearerSecurityOptions.DefaultMaximumConcurrentRequests.ToString());

builder.UseBearerTokenSecurity(new(
    RequiredSecret(AdministratorTokenEnvironmentVariable, AdministratorTokenFileEnvironmentVariable),
    OptionalSecret(WriterTokenEnvironmentVariable, WriterTokenFileEnvironmentVariable),
    OptionalSecret(ReaderTokenEnvironmentVariable, ReaderTokenFileEnvironmentVariable), requireHttps,
    maximumConcurrentRequests));

string profile = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable) ?? SemanticProfileName;

ConfigureProfile(builder, profile);

dataDirectory = await ConfigureGqlStorageAsync(builder, dataDirectory);

string instance = Environment.GetEnvironmentVariable("ENTITYFORGE_OBSERVABILITY_INSTANCE") ??
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        Environment.MachineName + "|" + Path.GetFullPath(dataDirectory) + "|" + profile))).ToLowerInvariant()[..32];

if (!EntityForgeObservability.ValidInstance(instance))
{
    throw new InvalidOperationException("Invalid observability instance ID.");
}

builder.ConfigureServices(services => services.AddSingleton(new EntityForgeObservabilityOptions(instance,
    Backend("ENTITYFORGE_PROMETHEUS_URL"), Backend("ENTITYFORGE_TEMPO_URL"))));

using EntityForgeDbOpenTelemetry? telemetry = EntityForgeDbOpenTelemetry.StartFromEnvironment(instance);

await using EntityForgeDbHost host = builder
    .ConfigureWebHost(web => web.UseUrls(
        Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://127.0.0.1:8080"))
    .Build();

await host.RunAsync();

static async Task<string> ConfigureGqlStorageAsync(EntityForgeDbHostBuilder builder, string dataDirectory)
{
    string? managed = Environment.GetEnvironmentVariable("ENTITYFORGE_DB_GQL_MANAGED_DIRECTORY");
    string? recovered = Environment.GetEnvironmentVariable("ENTITYFORGE_DB_GQL_RECOVERY_DIRECTORY");
    if (recovered is not null)
    {
        if (managed is not null || Environment.GetEnvironmentVariable("ENTITYFORGE_DB_GQL_DEFAULT_GRAPH") is not null)
        {
            throw new ArgumentException("Recovered GQL host configuration cannot be combined with managed-directory or default-graph overrides.");
        }
        var recovery = await EntityForgeDbRuntime.ReadRecoveredGqlHostAsync(recovered, new EntityForgeDbHostOptions());
        builder.UseLocalStorage(recovery.DataDirectory).UseGqlGraphs(recovery.Catalog)
            .ConfigureStorage(options => options.ManagedGqlDirectory = recovery.ManagedDirectory);
        return recovery.DataDirectory;
    }
    if (managed is not null)
    {
        builder.UseGqlGraphs(new(Environment.GetEnvironmentVariable("ENTITYFORGE_DB_GQL_DEFAULT_GRAPH") ?? "main", new Dictionary<string, string>()));
        builder.ConfigureStorage(options => options.ManagedGqlDirectory = managed);
    }
    return dataDirectory;
}

static void ConfigureProfile(EntityForgeDbHostBuilder builder, string profile)
{
    if (string.Equals(profile, PropertyGraphProfileName, StringComparison.OrdinalIgnoreCase))
    {
        builder.UsePropertyGraphProfile();
    }
    else if (string.Equals(profile, RdfProfileName, StringComparison.OrdinalIgnoreCase))
    {
        builder.UseRdfProfile();
    }
    else if (!string.Equals(profile, SemanticProfileName, StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException($"{ProfileEnvironmentVariable} must be 'semantic', 'property-graph', or 'rdf'.");
    }
}

static void RequireArgumentCount(string[] values, int expected, string usage)
{
    if (values.Length != expected)
    {
        throw new ArgumentException($"Usage: EntityForge.Db.Server {usage}");
    }
}

static string RequiredSecret(string valueVariable, string fileVariable) =>
    OptionalSecret(valueVariable, fileVariable) ??
    throw new InvalidOperationException($"{valueVariable} or {fileVariable} is required for server mode.");

static string? OptionalSecret(string valueVariable, string fileVariable)
{
    string? value = Environment.GetEnvironmentVariable(valueVariable);
    string? path = Environment.GetEnvironmentVariable(fileVariable);
    if (value is not null && path is not null)
    {
        throw new InvalidOperationException($"Configure only one of {valueVariable} and {fileVariable}.");
    }

    if (path is null)
    {
        return value;
    }

    string secret = File.ReadAllText(Path.GetFullPath(path)).Trim();
    return secret.Length == 0 ? throw new InvalidOperationException($"{fileVariable} points to an empty secret.") : secret;
}

static Uri? Backend(string variable)
{
    string? value = Environment.GetEnvironmentVariable(variable);
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (!Uri.TryCreate(value.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
        uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
    {
        throw new InvalidOperationException($"{variable} must be an HTTP(S) backend base URL without credentials, query or fragment.");
    }

    return uri;
}
