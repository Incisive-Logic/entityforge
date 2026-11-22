using System.Buffers;
using System.Net;
using System.Text.Json;
using EntityForge.Db.Mapping;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.Client;

// Shared by profile HTTP providers. This is deliberately not an additional public persistence API.
internal sealed class HttpEntityOperationTransport(HttpClient http, Guid incarnation, DatabaseProfileId profile, TimeSpan? attemptTimeout = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaximumResponseBytes = (EntityOperationSubmissionTransport.MaximumEnvelopeBytes / 3 + 1) * 4 + 64 * 1024;
    // Bound scratch space independently of payload size; pooling avoids a large allocation per call.
    private const int BufferBytes = 16 * 1024;
    private readonly TimeSpan deadline = ValidateTimeout(attemptTimeout ?? TimeSpan.FromSeconds(30));

    private static TimeSpan ValidateTimeout(TimeSpan value) => value > TimeSpan.Zero && value.TotalMilliseconds <= uint.MaxValue - 1
        ? value : throw new ArgumentOutOfRangeException(nameof(attemptTimeout));

    private CancellationTokenSource Attempt(CancellationToken cancellation)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        source.CancelAfter(deadline);
        return source;
    }

    internal async ValueTask<byte[]> PrepareAsync(Guid operation, byte[] envelope, CancellationToken cancellation, string action = "prepare")
    {
        var result = await Send(operation, action, envelope, cancellation).ConfigureAwait(false);
        if (result.Outcome != "Prepared" || result.Prepared is not { Length: > 0 } bytes ||
            bytes.Length > EntityOperationSubmissionTransport.MaximumEnvelopeBytes || result.Receipt is not null)
        {
            throw new InvalidDataException("Invalid prepared-operation response.");
        }

        return bytes;
    }

    internal async ValueTask ReleasePreparationAsync(Guid operation, byte[] envelope)
    {
        var result = await Send(operation, "release", envelope, CancellationToken.None).ConfigureAwait(false);
        if (result.Outcome != "Released" || result.Prepared is not null || result.Receipt is not null)
        {
            throw new InvalidDataException("Invalid preparation release acknowledgement.");
        }
    }

    internal async ValueTask<T> DeliverAsync<T>(Guid operation, byte[] envelope, bool prepared,
        Func<T, DatabaseCommitReceipt> nativeReceipt, CancellationToken cancellation, string? action = null) where T : class
    {
        cancellation.ThrowIfCancellationRequested();
        ValidateRequest(operation, envelope);
        try
        {
            var result = await Send(operation, action ?? (prepared ? "deliver" : "submit"), envelope, cancellation).ConfigureAwait(false);
            if (result.Prepared is not null)
            {
                throw new InvalidDataException("Delivery returned a preparation artifact.");
            }

            if (result.Outcome == "Aborted")
            {
                if (result.Receipt is not null)
                {
                    throw new InvalidDataException("An aborted response cannot carry a commit receipt.");
                }

                throw new EntityOperationAbortedException(operation);
            }
            if (result.Outcome is "Committed" or "CommittedNotConfirmedReadable")
            {
                T receipt = Receipt(result.Receipt, operation, nativeReceipt);
                if (result.Outcome == "CommittedNotConfirmedReadable")
                {
                    throw new EntityOperationCommittedException<T>(receipt, nativeReceipt(receipt),
                        new HttpRequestException("The remote commit succeeded but readability was not confirmed."));
                }

                return receipt;
            }
            throw new HttpRequestException(action == "bulk"
                ? "The initial-load outcome is uncertain; retry the exact initial-load session. Ordinary operation lookup does not resolve initial loads."
                : "The remote attempt did not establish a terminal outcome; resolve its original identity.");
        }
        catch (Exception failure) when (failure is not EntityOperationCommittedException<T> and not EntityOperationAbortedException)
        {
            if (action == "bulk" && failure is HttpRequestException)
            {
                var uncertain = new HttpRequestException("The initial-load attempt failed or its outcome is uncertain; retry the exact initial-load session. Ordinary operation lookup does not resolve initial loads.", failure, ((HttpRequestException)failure).StatusCode);
                EntityOperationSubmissionFailure.Attach(uncertain, operation, incarnation);
                throw uncertain;
            }
            EntityOperationSubmissionFailure.Attach(failure, operation, incarnation);
            throw;
        }
    }

    internal async ValueTask<EntityOperationResolution<T>> ResolveAsync<T>(Guid operation, bool abort,
        Func<T, DatabaseCommitReceipt> nativeReceipt, CancellationToken cancellation) where T : class
    {
        cancellation.ThrowIfCancellationRequested();
        ValidateIdentity(operation);
        try
        {
            return await ResolveCoreAsync(operation, abort, nativeReceipt, cancellation).ConfigureAwait(false);
        }
        catch (Exception failure) when (abort)
        {
            EntityOperationSubmissionFailure.Attach(failure, operation, incarnation);
            throw;
        }
    }

    private async ValueTask<EntityOperationResolution<T>> ResolveCoreAsync<T>(Guid operation, bool abort,
        Func<T, DatabaseCommitReceipt> nativeReceipt, CancellationToken cancellation) where T : class
    {
        ValidateIdentity(operation);
        using var attempt = Attempt(cancellation);
        cancellation = attempt.Token;
        using var request = new HttpRequestMessage(abort ? HttpMethod.Post : HttpMethod.Get, Route(operation, abort ? "abort" : null));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        var body = await ReadResponse(response, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.Gone)
        {
            throw Rejection(response, body);
        }

        var result = body.Deserialize<EntityOperationStatusTransport>(Json) ?? throw new InvalidDataException("Empty status response.");
        ValidateEnvelope(result.Version, result.DatabaseIncarnation, result.Profile, result.OperationId, operation);
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            if (result.State != "Expired" || result.Receipt is not null)
            {
                throw new InvalidDataException("Invalid expiry response.");
            }

            throw new EntityOperationExpiredException(operation);
        }
        return result.State switch
        {
            "Committed" => new(operation, true, Receipt(result.Receipt, operation, nativeReceipt)),
            "CommittedPendingReadability" => new(operation, true, Receipt(result.Receipt, operation, nativeReceipt), readable: false),
            "NotFound" when result.Receipt is null => new(operation, false, null),
            "PlanBound" when result.Receipt is null => new(operation, true, null),
            "Aborted" when result.Receipt is null => new(operation, true, null, true),
            _ => throw new InvalidDataException("Invalid operation status or receipt combination.")
        };
    }

    private T Receipt<T>(JsonElement? value, Guid operation, Func<T, DatabaseCommitReceipt> nativeReceipt) where T : class
    {
        if (value is not { ValueKind: JsonValueKind.Object } json)
        {
            throw new InvalidDataException("Missing operation receipt.");
        }

        foreach (var name in new[] { "DatabaseIncarnation", "TransactionId", "Version", "Outcome", "ChangeCursor", "EffectCount" })
        {
            Required(json, name);
        }

        Required(Required(json, "Version"), "Value");
        // Semantic results project their native profile through Receipt rather than a top-level Profile.
        if (typeof(T) == typeof(DatabaseCommitReceipt))
        {
            Required(json, "Profile");
        }

        var receipt = value?.Deserialize<T>(Json) ?? throw new InvalidDataException("Missing operation receipt.");
        var native = nativeReceipt(receipt);
        if (native.DatabaseIncarnation != incarnation || native.TransactionId != operation || native.Profile != profile)
        {
            throw new InvalidDataException("The receipt identifies a different operation or database.");
        }

        return receipt;
    }

    private async ValueTask<EntityOperationSubmissionTransport> Send(Guid operation, string action, byte[] envelope, CancellationToken cancellation)
    {
        ValidateRequest(operation, envelope);
        using var attempt = Attempt(cancellation);
        cancellation = attempt.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, Route(operation, action)) { Content = new ByteArrayContent(envelope) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        var body = await ReadResponse(response, cancellation).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && (body.ValueKind != JsonValueKind.Object || (!body.TryGetProperty("outcome", out _) && !body.TryGetProperty("Outcome", out _))))
        {
            throw Rejection(response, body);
        }

        var result = body.Deserialize<EntityOperationSubmissionTransport>(Json) ?? throw new InvalidDataException("Empty submission response.");
        ValidateEnvelope(result.Version, result.DatabaseIncarnation, result.Profile, result.OperationId, operation);
        bool consistent = result.Outcome switch
        {
            "Prepared" or "Committed" => response.StatusCode == HttpStatusCode.OK,
            "Released" => action == "release" && response.StatusCode == HttpStatusCode.OK,
            "CommittedNotConfirmedReadable" or "Uncertain" => response.StatusCode == HttpStatusCode.ServiceUnavailable,
            "Aborted" => response.StatusCode == HttpStatusCode.Conflict,
            "Expired" => response.StatusCode == HttpStatusCode.Gone,
            "AttemptFailed" => !response.IsSuccessStatusCode,
            _ => false
        };
        if (!consistent)
        {
            throw new InvalidDataException("Unknown or inconsistent operation response.");
        }

        if (result.Outcome == "Expired")
        {
            if (result.Receipt is not null || result.Prepared is not null)
            {
                throw new InvalidDataException("Expiry cannot carry a receipt or preparation.");
            }

            throw new EntityOperationExpiredException(operation);
        }
        if (result.Outcome is "AttemptFailed" or "Uncertain")
        {
            throw Rejection(response, body);
        }

        return result;
    }

    private static JsonElement Required(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Invalid receipt object.");
        }

        var matches = value.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidDataException($"Receipt requires exactly one {name} field.");
        }

        return matches[0].Value;
    }

    private static HttpRequestException Rejection(HttpResponseMessage response, JsonElement body)
    {
        string? code = null;
        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in body.EnumerateObject())
            {
                if (property.Name.Equals("code", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    code = property.Value.GetString();
                }
            }
        }

        var failure = new HttpRequestException("The HTTP attempt failed; this does not establish a logical abort.", null, response.StatusCode);
        if (code is not null)
        {
            failure.Data["Code"] = code;
        }

        if (response.Headers.TryGetValues("Retry-After", out var retry))
        {
            failure.Data["RetryAfter"] = string.Join(", ", retry);
        }

        return failure;
    }

    private static async Task<JsonElement> ReadResponse(HttpResponseMessage response, CancellationToken cancellation)
    {
        try
        {
            return await Read<JsonElement>(response, cancellation).ConfigureAwait(false);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            throw Rejection(response, default);
        }
    }

    private void ValidateEnvelope(int version, Guid database, string returnedProfile, Guid returnedOperation, Guid expected)
    {
        if (version != EntityOperationSubmissionTransport.CurrentVersion || database != incarnation ||
            returnedProfile != profile.ToString() || returnedOperation != expected)
        {
            throw new InvalidDataException("The response identifies a different destination, profile, operation or format.");
        }
    }

    private void ValidateIdentity(Guid operation)
    {
        if (incarnation == Guid.Empty || operation == Guid.Empty || !Enum.IsDefined(profile))
        {
            throw new ArgumentException("A destination, profile and operation identity are required.");
        }
    }

    private void ValidateRequest(Guid operation, byte[] envelope)
    {
        ValidateIdentity(operation);
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length is < 1 or > EntityOperationSubmissionTransport.MaximumEnvelopeBytes)
        {
            throw new ArgumentException("The application envelope exceeds its transport bounds.", nameof(envelope));
        }
    }

    private string Route(Guid operation, string? action) =>
        $"/v1/application/operations/{operation:D}" + (action is null ? "" : "/" + action) + $"?databaseIncarnation={incarnation:D}&expectedProfile={profile}";

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("Oversized application response.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellation).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaximumResponseBytes)
                {
                    throw new InvalidDataException("Oversized application response.");
                }

                output.Write(buffer, 0, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
        return JsonSerializer.Deserialize<T>(output.GetBuffer().AsSpan(0, checked((int)output.Length)), Json)
            ?? throw new InvalidDataException("Empty application response.");
    }
}
