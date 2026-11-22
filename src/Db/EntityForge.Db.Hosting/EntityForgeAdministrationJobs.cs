using System.Text.Json;
using EntityForge.Db.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace EntityForge.Db.Hosting;

internal sealed record AdministrationTarget(int Version, Guid DatabaseIncarnation, string Profile,
    string DataDirectory, string BackupDirectory);

internal sealed record AdministrationRequest(Guid DatabaseIncarnation, string Operation, string? BackupName, string ExpectedTarget);

internal sealed record AdministrationJob(int Version, Guid Id, Guid DatabaseIncarnation, string Operation,
    string Target, string State, DateTimeOffset UpdatedAt, JsonElement? Result, string? Message);

// A receipt is written before work starts. An interrupted Running receipt is deliberately Unknown
// after restart, never replayed. These receipts are administration metadata, not database transactions.
internal sealed class EntityForgeAdministrationJobs(EntityForgeDbHostOptions options,
    EntityForgeDbRuntime runtime, HostedEntityApplications applications, EntityForgeDbAdministrativeJobCoordinator coordinator)
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _active = [];
    private readonly string _directory = Path.Combine(Path.GetFullPath(options.DataDirectory), ".administration-jobs");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AdministrationTarget Target => new(1, applications.DatabaseIncarnation,
        runtime.Profile.ToString(), Path.GetFullPath(options.DataDirectory), Path.GetFullPath(options.BackupDirectory));

    public AdministrationJob? Lookup(Guid id)
    {
        lock (_gate)
        {
            string path = Path.Combine(_directory, id.ToString("N") + ".json");
            if (!File.Exists(path))
            {
                return null;
            }

            var job = JsonSerializer.Deserialize<AdministrationJob>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("Invalid administration receipt.");
            if (job.Id != id || job.DatabaseIncarnation != applications.DatabaseIncarnation)
            {
                throw new InvalidDataException("Administration receipt identity mismatch.");
            }

            return job.State == "Running" && !_active.Contains(id)
                ? job with
                {
                    State = "Unknown",
                    Message = "The host stopped before recording an outcome. Inspect the target; do not assume the operation failed or replay it."
                } : job;
        }
    }

    public async Task<AdministrationJob> Execute(Guid id, AdministrationRequest request)
    {
        if (id == Guid.Empty || request.DatabaseIncarnation != applications.DatabaseIncarnation)
        {
            throw new ArgumentException("The operation ID or database incarnation is invalid.");
        }

        if (request.Operation is not ("checkpoint" or "backup" or "gql-host-backup" or "optimize" or "reset-observations"))
        {
            throw new ArgumentException("Unsupported operation.");
        }

        if (request.Operation is "optimize" or "reset-observations" && runtime.Profile != DatabaseProfileId.Semantic)
        {
            throw new ArgumentException("Property optimization requires the Semantic profile.");
        }

        bool backup = request.Operation is "backup" or "gql-host-backup";
        if (request.Operation == "gql-host-backup" && runtime.Profile != DatabaseProfileId.PropertyGraph)
        {
            throw new ArgumentException("GQL host backups require the property-graph profile.");
        }
        if (backup && (string.IsNullOrWhiteSpace(request.BackupName) || request.BackupName.Length > 80 ||
            request.BackupName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
        {
            throw new ArgumentException("Backup names require 1–80 ASCII letters, digits, hyphens or underscores.");
        }

        string target = !backup ? Target.DataDirectory
            : Path.Combine(Target.BackupDirectory, $"{request.BackupName}-{id:N}." + (request.Operation == "gql-host-backup" ? "efgqlbackup" : "efbackup"));
        if (request.ExpectedTarget != target)
        {
            throw new ArgumentException("The reviewed target has changed.");
        }

        AdministrationJob job;
        lock (_gate)
        {
            var existing = Lookup(id);
            if (existing is not null)
            {
                if (existing.Operation != request.Operation || existing.Target != target)
                {
                    throw new ArgumentException("This job ID is already bound to a different request.");
                }

                return existing;
            }
            DurableDirectory.Create(_directory);
            if (Directory.EnumerateFiles(_directory, "*.json").Take(4096).Count() >= 4096)
            {
                throw new InvalidOperationException("Administration receipt capacity reached. Archive completed receipts before starting new work.");
            }

            job = new(1, id, request.DatabaseIncarnation, request.Operation, target, "Running", DateTimeOffset.UtcNow, null, null);
            Save(job); // No operation may start if its identity could not be recorded.
            _active.Add(id);
        }
        try
        {
            JsonElement result;
            if (request.Operation == "checkpoint")
            {
                result = JsonSerializer.SerializeToElement(new
                {
                    version = await runtime.CreateCheckpointAsync(CancellationToken.None)
                }, Json);
            }
            else if (request.Operation == "optimize")
            {
                result = await coordinator.ExecuteAsync(async token =>
                {
                    var applied = await runtime.Database.ApplyPropertyStorageOptimizationAsync(maximumChanges: 1, cancellationToken: token);
                    return JsonSerializer.SerializeToElement(new
                    {
                        applied.Analysis.AnalyzedAt,
                        applied.AppliedPlacements,
                        recommendationsConsidered = applied.Analysis.Recommendations.Count
                    }, Json);
                }, CancellationToken.None);
            }
            else if (request.Operation == "reset-observations")
            {
                result = await coordinator.ExecuteAsync(async token =>
                {
                    await runtime.Database.ResetPropertyStorageObservationsAsync(token);
                    return JsonSerializer.SerializeToElement(new
                    {
                        observationsCleared = true
                    }, Json);
                }, CancellationToken.None);
            }
            else if (request.Operation == "gql-host-backup")
            {
                await runtime.CreateGqlHostBackupAsync(target, CancellationToken.None);
                result = JsonSerializer.SerializeToElement(new
                {
                    kind = "gql-host",
                    directory = target
                }, Json);
            }
            else
            {
                // A job-private staging directory plus non-replacing publication protects existing backups.
                string staging = Path.Combine(Target.BackupDirectory, ".jobs", id.ToString("N"), "backup.efbackup");
                if (File.Exists(target))
                {
                    throw new IOException("The backup target already exists.");
                }

                DurableDirectory.Create(Path.GetDirectoryName(staging)!);
                var info = await runtime.CreateBackupAsync(staging, CancellationToken.None);
                File.Move(staging, target, overwrite: false);
                DurableDirectory.Sync(Target.BackupDirectory);
                result = JsonSerializer.SerializeToElement(info, Json);
            }
            job = job with
            {
                State = "Succeeded",
                Result = result,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        catch (Exception ex)
        {
            job = job with
            {
                State = "Failed",
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = ex switch
                {
                    EntityForgeDbAdministrativeJobRejectedException => "The host's administration concurrency limit was reached. This job did not start.",
                    EntityForgeDbAdministrativeJobTimeoutException => "The host time limit was reached. Work may have partially completed; inspect the target before starting another job.",
                    _ => "The operation could not complete. Work may have partially completed; inspect the target and server logs before starting another job."
                }
            };
        }
        finally
        {
            lock (_gate)
            {
                _active.Remove(id);
            }
        }
        lock (_gate)
        {
            // If saving fails, the on-disk Running receipt resolves as Unknown. Never report success.
            Save(job);
        }
        return job;
    }

    private void Save(AdministrationJob job)
    {
        string path = Path.Combine(_directory, job.Id.ToString("N") + ".json");
        string temporary = path + ".new";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, job, Json);
            stream.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
        DurableDirectory.Sync(_directory);
    }
}

internal static class EntityForgeAdministrationJobEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/v1/administration/target", async (HttpContext context, IEntityForgeDbAuthorizationPolicy policy,
            EntityForgeAdministrationJobs jobs) => await Run(context, policy, EntityForgeDbAdministrativeOperation.ReadStatus,
                () => Task.FromResult<IResult>(Results.Ok(jobs.Target))));
        endpoints.MapGet("/v1/administration/jobs/{id:guid}", async (Guid id, Guid databaseIncarnation, HttpContext context,
            IEntityForgeDbAuthorizationPolicy policy, EntityForgeAdministrationJobs jobs) =>
            await Run(context, policy, EntityForgeDbAdministrativeOperation.ReadStatus, () =>
            {
                if (databaseIncarnation != jobs.Target.DatabaseIncarnation)
                {
                    return Task.FromResult<IResult>(Results.Conflict());
                }

                var job = jobs.Lookup(id);
                return Task.FromResult<IResult>(job is null ? Results.NotFound() : Results.Ok(job));
            }));
        endpoints.MapPost("/v1/administration/jobs/{id:guid}", async (Guid id, AdministrationRequest request,
            HttpContext context, IEntityForgeDbAuthorizationPolicy policy, EntityForgeAdministrationJobs jobs) =>
            await Run(context, policy, request.Operation switch
            {
                "backup" or "gql-host-backup" => EntityForgeDbAdministrativeOperation.CreateBackup,
                "optimize" => EntityForgeDbAdministrativeOperation.ApplyPropertyStorageOptimization,
                "reset-observations" => EntityForgeDbAdministrativeOperation.ResetPropertyStorageObservations,
                _ => EntityForgeDbAdministrativeOperation.CreateCheckpoint
            }, async () => Results.Ok(await jobs.Execute(id, request))));
    }

    private static async Task<IResult> Run(HttpContext context, IEntityForgeDbAuthorizationPolicy policy,
        EntityForgeDbAdministrativeOperation operation, Func<Task<IResult>> action)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!await policy.AuthorizeAsync(context, operation, context.RequestAborted))
        {
            return Results.StatusCode(403);
        }

        try
        {
            return await action();
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, title: "Invalid job request or conflicting identity.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return Results.Problem(statusCode: 503, title: "Administration receipts unavailable. Outcome unknown; resolve the same job ID before taking further action.");
        }
    }
}
