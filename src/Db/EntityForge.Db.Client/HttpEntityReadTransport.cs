using System.Buffers;
using System.Net;
using System.Text.Json;
using EntityForge.Db.Mapping;

namespace EntityForge.Db.Client;

// Internal transport seam; profile-specific decoding/materialization is composed above this lifecycle.
internal sealed class HttpEntityReadTransport
{
    private const int MaximumRequestBytes = 1024 * 1024;
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    // Bound scratch space independently of payload size; pooling avoids a large allocation per call.
    private const int BufferBytes = 16 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    private readonly Guid database;
    private readonly DatabaseProfileId profile;
    private readonly IEntityReadRecipePolicy policy;
    private readonly TimeSpan timeout;

    internal HttpEntityReadTransport(HttpClient http, Guid database, IEntityReadRecipePolicy policy, TimeSpan? attemptTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(policy);
        var profile = policy.Profile;
        this.policy = policy;
        if (database == Guid.Empty || !Enum.IsDefined(profile))
        {
            throw new ArgumentException("A database and profile are required.");
        }

        this.http = http;
        this.database = database;
        this.profile = profile;
        timeout = attemptTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptTimeout));
        }
    }

    internal async ValueTask<HttpEntityReadScope> OpenAsync(IReadOnlyList<EntityMappingManifest> manifests, TimeSpan duration,
        Uri? sourceSystem = null, Uri? sourcePartition = null, CancellationToken cancellation = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var response = await Send(null, null, new
        {
            manifests,
            durationMilliseconds = checked((int)duration.TotalMilliseconds),
            sourceSystem,
            sourcePartition
        }, cancellation);
        var id = ReadIdentity(Require(response, "scopeId"));
        if (id == Guid.Empty)
        {
            throw new InvalidDataException("An open reply requires a scope identity.");
        }

        try
        {
            var version = Check(response, id, null);
            var guard = Require(response, "contributionGuard").Clone();
            return new(this, id, version, guard);
        }
        catch (Exception primary)
        {
            // A malformed reply from the expected destination may still own a handle. Never select a replacement view.
            if (MatchesDestination(response))
            {
                try
                {
                    await Release(id);
                }
                catch (Exception release)
                {
                    primary.Data["ReadScopeReleaseFailure"] = release;
                }
            }

            throw;
        }
    }

    private bool MatchesDestination(JsonElement body) => body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty("databaseIncarnation", out var db) && db.ValueKind == JsonValueKind.String && db.TryGetGuid(out var value) && value == database &&
        body.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == profile.ToString();

    internal DatabaseVersion Check(JsonElement body, Guid id, DatabaseVersion? version)
    {
        var snapshot = Require(body, "snapshotVersion");
        if (!MatchesDestination(body) || ReadIdentity(Require(body, "scopeId")) != id ||
            snapshot.ValueKind != JsonValueKind.Number || !snapshot.TryGetInt64(out long number) || number < 0 || version is { } expected && number != expected.Value)
        {
            throw new InvalidDataException("The response differs from the retained scope, destination or version.");
        }
        // Require uniqueness as well as value equality for the identity fields.
        Require(body, "databaseIncarnation");
        Require(body, "profile");
        return new(number);
    }

    internal async ValueTask<HttpEntityReadScope> CompleteAsync(IReadOnlyList<EntityMappingManifest> manifests,
        EntityReadRecipe recipe, Uri? sourceSystem, Uri? sourcePartition, TimeSpan duration, CancellationToken cancellation)
    {
        recipe.Validate(policy);
        var response = await Send(null, "complete", new
        {
            scope = new
            {
                manifests,
                durationMilliseconds = checked((int)duration.TotalMilliseconds),
                sourceSystem,
                sourcePartition
            },
            recipe
        }, cancellation).ConfigureAwait(false);
        var version = Check(response, recipe.ReadId, null);
        var guard = Require(response, "contributionGuard").Clone();
        var included = Require(response, "includesContributionGuard");
        if (included.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            included.GetBoolean() != (policy.SupportsContributionGuard && recipe.IncludeContributionGuard) ||
            !included.GetBoolean() && guard.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidDataException("The complete response differs from its requested contribution-guard mode.");
        }

        var items = Require(response, "observations");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > EntityReadRecipe.MaximumObservations)
        {
            throw new InvalidDataException("Invalid complete-read observations.");
        }

        var observations = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            string kind = Require(item, "kind").GetString() ?? throw new InvalidDataException("Missing observation kind.");
            if (!observations.TryAdd(EntityReadRecipe.Key(kind, Require(item, "query")), Require(item, "value").Clone()))
            {
                throw new InvalidDataException("Duplicate complete-read observation.");
            }
        }
        return new(this, recipe.ReadId, version, guard, observations, response.Clone());
    }

    private static Guid ReadIdentity(JsonElement value) => value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id)
        ? id : throw new InvalidDataException("Invalid scope identity.");

    internal static JsonElement Require(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Invalid scope response.");
        }

        var members = value.EnumerateObject().Where(p => p.Name == name).ToArray();
        if (members.Length != 1)
        {
            throw new InvalidDataException($"Scope response requires exactly one {name}.");
        }

        return members[0].Value;
    }

    internal async ValueTask<JsonElement> Read(Guid id, string kind, object query, CancellationToken cancellation) =>
        await Send(id, "read", new
        {
            kind,
            query
        }, cancellation);

    internal async ValueTask<JsonElement> Validate(Guid id, CancellationToken cancellation) => await Send(id, "validate", null, cancellation);

    internal async ValueTask Release(Guid id)
    {
        const int MaximumAttempts = 2;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await Send(id, "release", null, CancellationToken.None);
                return;
            }
            catch (Exception error) when (attempt < MaximumAttempts &&
                (error is OperationCanceledException || error is HttpRequestException httpFailure &&
                    (httpFailure.StatusCode is null || (int)httpFailure.StatusCode >= 500)))
            {
            }
        }
    }

    private async ValueTask<JsonElement> Send(Guid? id, string? action, object? body, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        string route = "/v1/application/read-scopes" + (id is null ? action is null ? "" : $"/{action}" : $"/{id:D}/{action}") +
            $"?databaseIncarnation={database:D}&expectedProfile={profile}";
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (body is not null)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
            if (bytes.Length > MaximumRequestBytes)
            {
                throw new ArgumentException("Oversized scope request.");
            }

            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new("application/json");
        }
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (action == "release" && response.StatusCode == HttpStatusCode.NoContent)
        {
            return default;
        }

        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("Oversized scope response.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, BufferBytes), deadline.Token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumResponseBytes)
                {
                    throw new InvalidDataException("Oversized scope response.");
                }

                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        JsonElement result;
        try
        {
            result = JsonSerializer.Deserialize<JsonElement>(output.GetBuffer().AsSpan(0, checked((int)output.Length)), Json);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Read scope transport failed.", null, response.StatusCode);
        }
        if (!response.IsSuccessStatusCode)
        {
            var failure = new HttpRequestException("The retained read is unavailable or failed.", null, response.StatusCode);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
            {
                failure.Data["Code"] = code.GetString();
            }

            throw failure;
        }
        if (action == "release" || response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidDataException("Unexpected scope response status.");
        }

        return result;
    }
}

internal sealed class HttpEntityReadScope(HttpEntityReadTransport transport, Guid id, DatabaseVersion version, JsonElement guard,
    IReadOnlyDictionary<string, JsonElement>? observations = null, JsonElement completedResponse = default) : IAsyncDisposable
{
    internal Guid Id => id;

    internal DatabaseVersion Version => version;

    internal JsonElement ContributionGuard => guard;

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private bool retired;
    private Task? disposal;

    private sealed class MaterializationContext
    {
        internal int Active = 1;
    }

    private readonly AsyncLocal<MaterializationContext?> materializing = new();

    private bool IsMaterializing => materializing.Value is { } context && Volatile.Read(ref context.Active) != 0;

    internal async ValueTask<T> MaterializeAsync<T>(Func<ValueTask<T>> readAndValidate)
    {
        if (IsMaterializing)
        {
            throw new InvalidOperationException("Nested materialization is not supported.");
        }

        var context = new MaterializationContext();
        materializing.Value = context;
        try
        {
            return await readAndValidate().ConfigureAwait(false);
        }
        finally
        {
            // A captured child ExecutionContext must not retain permission to defer validation.
            Volatile.Write(ref context.Active, 0);
            materializing.Value = null;
        }
    }

    internal ValueTask<JsonElement> ReadAsync(string kind, object query, CancellationToken cancellation = default) =>
        UseAsync(token => ReadResponse(kind, query, token), response => HttpEntityReadTransport.Require(response, "value").Clone(), false, cancellation);

    internal ValueTask<T> ReadAsync<T>(string kind, object query, Func<JsonElement, T> decode, CancellationToken cancellation = default) =>
        UseAsync(token => ReadResponse(kind, query, token), response => decode(HttpEntityReadTransport.Require(response, "value")), true, cancellation);

    internal async ValueTask ValidateAsync(CancellationToken cancellation = default) =>
        _ = await UseAsync(ValidateResponse, _ => true, false, cancellation);

    internal ValueTask<T> ReadGuardAsync<T>(Func<JsonElement, T> decode, CancellationToken cancellation = default) =>
        UseAsync(ValidateResponse, response =>
        {
            var original = decode(guard);
            if (!EqualityComparer<T>.Default.Equals(original, decode(HttpEntityReadTransport.Require(response, "contributionGuard"))))
            {
                throw new InvalidDataException("Contribution guard changed within a retained view.");
            }

            return original;
        }, false, cancellation);

    private ValueTask<JsonElement> ValidateResponse(CancellationToken token) => observations is null
        ? transport.Validate(id, token) : ValueTask.FromResult(completedResponse);

    private ValueTask<JsonElement> ReadResponse(string kind, object query, CancellationToken token)
    {
        if (observations is null)
        {
            return transport.Read(id, kind, query, token);
        }

        if (!observations.TryGetValue(EntityReadRecipe.Key(kind, EntityReadRecipe.Value(query)), out var value))
        {
            throw new InvalidDataException("The completed read omitted a required observation.");
        }
        // Preserve all destination/version framing and existing profile decoders. No fallback
        // fetch is permitted: a missing observation cannot select another database view.
        return ValueTask.FromResult(EntityReadRecipe.Value(new
        {
            scopeId = id,
            databaseIncarnation = HttpEntityReadTransport.Require(completedResponse, "databaseIncarnation"),
            profile = HttpEntityReadTransport.Require(completedResponse, "profile"),
            snapshotVersion = version.Value,
            value
        }));
    }

    private async ValueTask<T> UseAsync<T>(Func<CancellationToken, ValueTask<JsonElement>> action, Func<JsonElement, T> decode,
        bool validateAfterDecode, CancellationToken cancellation)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(retired, this);
        }

        bool entered = false;
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
            await gate.WaitAsync(attempt.Token).ConfigureAwait(false);
            try
            {
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(retired, this);
                    entered = true;
                }
                attempt.Token.ThrowIfCancellationRequested();
                var response = await action(attempt.Token).ConfigureAwait(false);
                transport.Check(response, id, version);
                var value = decode(response);
                if (validateAfterDecode && !IsMaterializing)
                {
                    transport.Check(await ValidateResponse(attempt.Token).ConfigureAwait(false), id, version);
                }

                attempt.Token.ThrowIfCancellationRequested();
                return value;
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception primary)
        {
            primary.Data["ReadScopeId"] = id;
            var cleanup = DisposeAsync();
            try
            {
                if (entered)
                {
                    await cleanup;
                }
            }
            catch (Exception release)
            {
                primary.Data["ReadScopeReleaseFailure"] = release;
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (sync)
        {
            retired = true;
            if (disposal is { IsFaulted: false, IsCanceled: false })
            {
                return new(disposal);
            }

            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
        }
        _ = Task.Run(() => ReleaseAsync(completion));
        _ = ObserveDisposalAsync(completion.Task);
        return new(completion.Task);
    }

    private static async Task ObserveDisposalAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            /* Retained on disposal for explicit retry; rejected readers need not await it. */
        }
    }

    private async Task ReleaseAsync(TaskCompletionSource completion)
    {
        try
        {
            try
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (observations is null)
                    {
                        await transport.Release(id).ConfigureAwait(false);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
            completion.TrySetResult();
        }
        catch (Exception failure)
        {
            completion.TrySetException(failure);
        }
    }
}
