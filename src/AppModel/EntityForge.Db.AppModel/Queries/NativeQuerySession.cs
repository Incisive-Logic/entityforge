using System.Net.Http.Headers;
using System.Text.Json;
using EntityForge.Db.Client;
using EntityForge.Db.Transport.Contracts;

namespace EntityForge.Db.AppModel.Queries;

/// <summary>Identifies the query interface used by an application query executor.</summary>
public enum NativeQueryLanguage
{
    /// <summary>GQL queries on the property-graph profile.</summary>
    Gql,

    /// <summary>SPARQL queries on the RDF profile.</summary>
    Sparql,

    /// <summary>GraphQL application operations, which may include mutations.</summary>
    GraphQl
}

/// <summary>A query response and, when supplied by GraphQL, its decoded entity projection.</summary>
/// <param name="Language">The interface that produced the response.</param>
/// <param name="Document">The response document. The HTTP executor returns a clone independent of its disposed response parser.</param>
/// <param name="Entities">Optional entity projection used for entity inspection and continuation paging.</param>
public sealed record NativeQueryResult(NativeQueryLanguage Language, JsonElement Document,
    EntityProjectionQueryResult? Entities = null);

/// <summary>Executes query text through one configured language interface.</summary>
public interface INativeQueryExecutor
{
    /// <summary>Gets the configured language interface.</summary>
    NativeQueryLanguage Language
    {
        get;
    }

    /// <summary>Executes the supplied text and returns its response.</summary>
    /// <param name="query">The query or operation text.</param>
    /// <param name="cancellationToken">Cancels waiting; it does not establish rollback of a server mutation.</param>
    /// <returns>The completed response.</returns>
    ValueTask<NativeQueryResult> ExecuteAsync(string query, CancellationToken cancellationToken = default);
}

/// <summary>Adds JSON variables to GraphQL operation execution.</summary>
public interface IGraphQlQueryExecutor : INativeQueryExecutor
{
    /// <summary>Executes an operation with its variable values.</summary>
    /// <param name="query">The GraphQL operation text.</param>
    /// <param name="variables">A JSON object containing variable values.</param>
    /// <param name="cancellationToken">Cancels waiting without guaranteeing mutation rollback.</param>
    /// <returns>The completed response.</returns>
    ValueTask<NativeQueryResult> ExecuteAsync(string query, string variables, CancellationToken cancellationToken = default);
}

/// <summary>Uses profile query endpoints and GraphQL operations. Owns the response and bounds allocation.</summary>
/// <param name="client">Caller-owned client with a base address and any required authentication. This executor does not dispose it.</param>
/// <param name="language">The interface to call. GQL and SPARQL use their read query routes; GraphQL may execute mutations.</param>
/// <remarks>Requests have a thirty-second deadline. Responses are buffered within the byte limit, not exposed as a streaming scan.</remarks>
public sealed class HttpNativeQueryExecutor(HttpClient client, NativeQueryLanguage language) : IGraphQlQueryExecutor
{
    /// <summary>Maximum UTF-16 character count for query text and, separately, GraphQL variable JSON.</summary>
    public const int MaximumQueryCharacters = 64 * 1024;

    /// <summary>Maximum response-body byte count, including error responses.</summary>
    public const int MaximumResponseBytes = 4 * 1024 * 1024;
    private const int ReadBufferBytes = 16 * 1024;
    private const int MaximumErrorCharacters = 2048;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public NativeQueryLanguage Language => language;

    /// <summary>Executes text using an empty GraphQL variable object when applicable.</summary>
    /// <param name="query">The query or GraphQL operation text.</param>
    /// <param name="cancellationToken">Cancels waiting; server work may already have completed.</param>
    /// <returns>A response whose JSON remains valid after the HTTP response is disposed.</returns>
    public ValueTask<NativeQueryResult> ExecuteAsync(string query, CancellationToken cancellationToken = default) =>
        ExecuteAsync(query, "{}", cancellationToken);

    /// <summary>Executes a bounded HTTP request and validates the response shape.</summary>
    /// <param name="query">Nonblank text within <see cref="MaximumQueryCharacters"/>.</param>
    /// <param name="variables">GraphQL variable JSON, or blank for an empty object. Ignored for GQL and SPARQL; this is not their parameter-binding API.</param>
    /// <param name="cancellationToken">Cancels waiting, combined with the executor's request deadline.</param>
    /// <returns>A cloned JSON response and any decoded GraphQL entity projection.</returns>
    /// <exception cref="ArgumentException">Text is blank or too long, or GraphQL variables are too long or not an object.</exception>
    /// <exception cref="JsonException">Variable JSON or response JSON is malformed.</exception>
    /// <exception cref="HttpRequestException">Transport fails or the host returns an unsuccessful status.</exception>
    /// <exception cref="InvalidDataException">The response is oversized, has an unsupported shape, or reports GraphQL errors.</exception>
    /// <exception cref="OperationCanceledException">Cancellation or a request deadline stops waiting.</exception>
    /// <remarks>GraphQL errors are surfaced even if the document also contains partial data. Cancellation does not confirm that a mutation was rolled back.</remarks>
    public async ValueTask<NativeQueryResult> ExecuteAsync(string query, string variables, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        cancellationToken = timeout.Token;
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > MaximumQueryCharacters)
        {
            throw new ArgumentException("The query exceeds Studio's text limit.");
        }

        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("query", query);
            if (language == NativeQueryLanguage.GraphQl)
            {
                if (variables.Length > MaximumQueryCharacters)
                {
                    throw new ArgumentException("Variables exceed Studio's text limit.");
                }

                using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(variables) ? "{}" : variables);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ArgumentException("Variables must be a JSON object.");
                }

                writer.WritePropertyName("variables");
                parsed.RootElement.WriteTo(writer);
                writer.WriteStartObject("extensions");
                writer.WriteBoolean("entityDocument", true);
                writer.WriteEndObject();
            }
            if (language == NativeQueryLanguage.Gql)
            {
                writer.WriteBoolean("includeSupportingGraph", true);
            }

            writer.WriteEndObject();
        }
        using var request = new HttpRequestMessage(HttpMethod.Post,
            language switch
            {
                NativeQueryLanguage.Gql => "v1/graph/gql/query",
                NativeQueryLanguage.GraphQl => "graphql",
                _ => "v1/rdf/sparql"
            });
        request.Content = new ByteArrayContent(body.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException("The response exceeds Studio's 4 MiB limit. Narrow the query or reduce LIMIT.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[ReadBufferBytes];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > MaximumResponseBytes)
            {
                throw new InvalidDataException("The response exceeds Studio's 4 MiB limit. Narrow the query or reduce LIMIT.");
            }

            bytes.Write(buffer, 0, count);
        }
        if (!response.IsSuccessStatusCode)
        {
            string detail = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Authentication failed. Reconnect with a valid token that grants query access.",
                System.Net.HttpStatusCode.Forbidden => "This credential is not permitted to run the query. Check its permissions with the database administrator.",
                _ => "Check query syntax, supported language features and permissions."
            };
            try
            {
                using var error = JsonDocument.Parse(bytes.ToArray());
                if (error.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (string property in new[] { "detail", "message" })
                    {
                        if (!error.RootElement.TryGetProperty(property, out var message) ||
                            message.ValueKind != JsonValueKind.String || String.IsNullOrWhiteSpace(message.GetString()))
                        {
                            continue;
                        }

                        string text = message.GetString()!;
                        detail = text.Length <= MaximumErrorCharacters ? text : text[..MaximumErrorCharacters] + "…";
                        break;
                    }
                }
            }
            catch (JsonException)
            {
            }
            throw new HttpRequestException($"Query rejected by the host (HTTP {(int)response.StatusCode}). {detail}", null, response.StatusCode);
        }
        using var document = JsonDocument.Parse(bytes.ToArray());
        JsonElement root = document.RootElement;
        if (language == NativeQueryLanguage.GraphQl)
        {
            if (root.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
            {
                string detail = string.Join(Environment.NewLine, errors.EnumerateArray().Select(error =>
                    error.GetProperty("message").GetString() + (error.TryGetProperty("locations", out var locations)
                        ? " " + locations.GetRawText() : "")));
                throw new InvalidDataException(detail.Length > MaximumErrorCharacters ? detail[..MaximumErrorCharacters] : detail);
            }
            if (!root.TryGetProperty("data", out _))
            {
                throw new InvalidDataException("The host returned an unsupported GraphQL result.");
            }

            EntityProjectionQueryResult? entities = null;
            if (root.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("entityDocument", out var projection))
            {
                entities = EntityProjectionTransport.Decode(projection.Deserialize(
                    QueryJsonContext.Default.EntityProjectionTransportResponse)!);
            }

            return new(language, root.Clone(), entities);
        }
        bool recognized = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("snapshotVersion", out _) &&
            (language == NativeQueryLanguage.Gql
                ? HasArray(root, "matches") && HasArray(root, "nodes") && HasArray(root, "edges")
                : HasArray(root, "bindings") || HasArray(root, "quads") ||
                  root.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False);
        if (!recognized)
        {
            throw new InvalidDataException("The host returned an unsupported native result shape.");
        }

        return new(language, root.Clone());
    }

    private static bool HasArray(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array;
}

/// <summary>Connection-scoped operation lifecycle; cancellation never grants a stale response publication rights.
/// Cancelling a GraphQL mutation does not establish that its database effects were rolled back.</summary>
public sealed class NativeQuerySession : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _execution;
    private INativeQueryExecutor? _executor;

    /// <summary>Raised when execution, paging or binding state changes. Callbacks are not marshalled to a UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when query text or variable text changes, including binding resets.</summary>
    public event EventHandler? DraftChanged;

    /// <summary>Gets the current execution state. A successful result may contain no rows.</summary>
    public EntityQueryExecutionState State
    {
        get; private set;
    }

    private string _draft = String.Empty;
    private string _variables = "{}";

    /// <summary>Gets or sets editable query text; changing it does not automatically execute or discard the last result.</summary>
    public string Draft
    {
        get => _draft; set
        {
            if (_draft == value)
            {
                return;
            }

            _draft = value;
            DraftChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Gets or sets editable GraphQL variable JSON; validation occurs when executing.</summary>
    public string Variables
    {
        get => _variables; set
        {
            if (_variables == value)
            {
                return;
            }

            _variables = value;
            DraftChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Gets the text captured for the last execution attempt, or null after reset.</summary>
    public string? ExecutedDraft
    {
        get; private set;
    }

    /// <summary>Gets the variable text captured for the last execution attempt, or null after reset.</summary>
    public string? ExecutedVariables
    {
        get; private set;
    }

    /// <summary>Gets whether the editable text differs from the last execution attempt.</summary>
    public bool HasDraftChanges => ExecutedDraft is not null && (Draft != ExecutedDraft || Variables != ExecutedVariables);

    /// <summary>Gets the displayed result. New execution clears it; a failed continuation retains the previous page.</summary>
    public NativeQueryResult? Result
    {
        get; private set;
    }

    /// <summary>Gets a displayable execution or paging error, or null when none is recorded.</summary>
    public string? Error
    {
        get; private set;
    }

    private readonly List<NativeQueryResult> _pages = [];
    private int _firstRetainedPage = 1;

    /// <summary>Gets the one-based displayed page number, or zero when no page is retained.</summary>
    public int PageNumber
    {
        get; private set;
    }

    /// <summary>Gets whether a continuation request is in progress.</summary>
    public bool IsPaging
    {
        get; private set;
    }

    /// <summary>Gets whether a previous page remains in the bounded local history and navigation is available.</summary>
    public bool CanPreviousPage => !IsPaging && State != EntityQueryExecutionState.Running && PageNumber > _firstRetainedPage;

    /// <summary>Gets whether a cached next page or supported GraphQL entity continuation is available.</summary>
    public bool CanNextPage => !IsPaging && State != EntityQueryExecutionState.Running && Result?.Entities is not null &&
        (PageNumber < _firstRetainedPage + _pages.Count - 1 || _executor is IGraphQlQueryExecutor && Result.Entities.NextContinuation is not null && ExecutedDraft is not null && GraphQlPaging.CanContinue(ExecutedDraft));

    private void ClearPages()
    {
        _pages.Clear();
        _firstRetainedPage = 1;
        PageNumber = 0;
        IsPaging = false;
    }

    /// <summary>Displays the previous cached page, or does nothing when navigation is unavailable.</summary>
    public void PreviousPage()
    {
        lock (_gate)
        {
            if (!CanPreviousPage)
            {
                return;
            }

            Result = _pages[--PageNumber - _firstRetainedPage];
            Error = null;
            State = EntityQueryExecutionState.Succeeded;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Displays the next cached page or requests a GraphQL entity continuation at the same snapshot.</summary>
    /// <returns>Completion of navigation, or an already completed operation when navigation is unavailable.</returns>
    /// <remarks>Retains at most ten pages. Request failures are recorded in <see cref="Error"/> while the previous page remains displayed. Use <see cref="Cancel"/> to stop waiting.</remarks>
    public async ValueTask NextPageAsync()
    {
        IGraphQlQueryExecutor executor;
        CancellationTokenSource current;
        (string Query, string Variables) request;
        long snapshot;
        lock (_gate)
        {
            if (!CanNextPage)
            {
                return;
            }

            if (PageNumber < _firstRetainedPage + _pages.Count - 1)
            {
                Result = _pages[++PageNumber - _firstRetainedPage];
                Error = null;
                State = EntityQueryExecutionState.Succeeded;
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
            executor = _executor as IGraphQlQueryExecutor ?? throw new InvalidOperationException("GraphQL paging is unavailable.");
            snapshot = Result!.Entities!.SnapshotVersion.Value;
            request = GraphQlPaging.Continue(ExecutedDraft!, ExecutedVariables ?? "{}", Result.Entities.NextContinuation!, snapshot);
            current = new CancellationTokenSource();
            _execution = current;
            IsPaging = true;
            Error = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var page = await executor.ExecuteAsync(request.Query, request.Variables, current.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_execution != current)
                {
                    return;
                }

                current.Token.ThrowIfCancellationRequested();
                if (page.Entities is null || page.Entities.SnapshotVersion.Value != snapshot)
                {
                    throw new InvalidDataException("The host did not retain the query snapshot. Run the query again.");
                }

                _pages.Add(page);
                PageNumber++;
                Result = page;
                // Keep a bounded backwards history; each native response is already limited to 4 MiB.
                if (_pages.Count > 10)
                {
                    _pages.RemoveAt(0);
                    _firstRetainedPage++;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_execution != current)
                {
                    return;
                }

                Error = ex is OperationCanceledException ? "Page loading stopped. The previous page is retained." : ex.Message;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_execution == current)
                {
                    _execution = null;
                    IsPaging = false;
                }
                current.Dispose();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Gets whether an executor is bound and no query or continuation is running.</summary>
    public bool CanExecute => _executor is not null && !IsPaging && State != EntityQueryExecutionState.Running;

    /// <summary>Gets the bound executor's language, or null when disconnected.</summary>
    public NativeQueryLanguage? Language => _executor?.Language;

    /// <summary>Changes the executor, cancelling pending work and resetting results, variables and the language's initial draft.</summary>
    /// <param name="executor">The new executor, or null to disconnect. Binding the same instance does nothing.</param>
    public void Bind(INativeQueryExecutor? executor)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_executor, executor))
            {
                return;
            }

            var previous = _execution;
            _execution = null;
            previous?.Cancel();
            _executor = executor;
            ClearPages();
            Result = null;
            Error = null;
            State = EntityQueryExecutionState.Idle;
            ExecutedDraft = null;
            ExecutedVariables = null;
            Variables = "{}";
            Draft = executor?.Language switch
            {
                NativeQueryLanguage.Gql => "MATCH (n)\nRETURN n\nLIMIT 50",
                NativeQueryLanguage.Sparql => "SELECT ?s ?p ?o\nWHERE {\n  ?s ?p ?o\n}\nLIMIT 50",
                NativeQueryLanguage.GraphQl => QueryTextFormatting.GraphQl("{ __schema { queryType { name } } }"),
                _ => String.Empty
            };
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Captures and executes the current draft, clearing previous results before starting.</summary>
    /// <param name="cancellationToken">Cancels waiting without establishing server-side rollback.</param>
    /// <returns>Completion of the attempt; ordinary executor failures are recorded in <see cref="State"/> and <see cref="Error"/>.</returns>
    /// <remarks>Does nothing when <see cref="CanExecute"/> is false. Responses from a replaced or cancelled execution cannot publish a result.</remarks>
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        INativeQueryExecutor executor;
        string draft;
        string variables;
        CancellationTokenSource current;
        lock (_gate)
        {
            if (!CanExecute)
            {
                return;
            }

            executor = _executor!;
            draft = Draft;
            variables = Variables;
            ExecutedDraft = draft;
            ExecutedVariables = variables;
            current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _execution = current;
            ClearPages();
            Result = null;
            Error = null;
            State = EntityQueryExecutionState.Running;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            var result = executor.Language == NativeQueryLanguage.GraphQl && executor is IGraphQlQueryExecutor graphQl
                ? await graphQl.ExecuteAsync(draft, variables, current.Token).ConfigureAwait(false)
                : await executor.ExecuteAsync(draft, current.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_execution != current)
                {
                    return;
                }

                current.Token.ThrowIfCancellationRequested();
                Result = result;
                _pages.Add(result);
                PageNumber = 1;
                State = EntityQueryExecutionState.Succeeded;
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (_execution != current)
                {
                    return;
                }

                State = exception is OperationCanceledException ? EntityQueryExecutionState.Cancelled : EntityQueryExecutionState.Failed;
                Error = exception is OperationCanceledException
                    ? (current.IsCancellationRequested ? "Stopped waiting for the query." : "The query timed out or the request was cancelled.") +
                      (executor.Language == NativeQueryLanguage.GraphQl
                          ? " If this operation included a mutation, its changes may have completed. Cancellation does not confirm rollback. Verify the outcome before retrying."
                          : " Server execution may still be in progress.")
                    : exception.Message;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_execution == current)
                {
                    _execution = null;
                }

                current.Dispose();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Refreshes transport bindings, preserving text and variables for a continuing connection.</summary>
    /// <param name="executor">The replacement executor, or null when transport is unavailable.</param>
    /// <param name="invalidateResults">Whether to cancel pending work and clear results and execution history.</param>
    /// <remarks>A different non-null language performs a full <see cref="Bind"/> reset. Without invalidation, a pending execution and displayed result are retained.</remarks>
    public void RefreshBinding(INativeQueryExecutor? executor, bool invalidateResults = false)
    {
        lock (_gate)
        {
            if (_executor?.Language != executor?.Language && executor is not null)
            {
                Bind(executor);
                return;
            }
            _executor = executor;
            if (!invalidateResults)
            {
                return;
            }

            var previous = _execution;
            _execution = null;
            previous?.Cancel();
            ClearPages();
            Result = null;
            ExecutedDraft = null;
            ExecutedVariables = null;
            Error = null;
            State = EntityQueryExecutionState.Idle;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops waiting and prevents the pending execution from publishing a result.</summary>
    /// <remarks>A cancelled continuation retains the previous page. Server work may continue; cancelling a GraphQL mutation does not prove rollback.</remarks>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_execution is not { } current)
            {
                return;
            }

            _execution = null;
            current.Cancel();
            if (IsPaging)
            {
                IsPaging = false;
                Error = "Page loading stopped. The previous page is retained.";
                Changed?.Invoke(this, EventArgs.Empty);
                return;
            }
            State = EntityQueryExecutionState.Cancelled;
            Error = "Stopped waiting for the query." + (Language == NativeQueryLanguage.GraphQl
                ? " If this operation included a mutation, its changes may have completed. Cancellation does not confirm rollback. Verify the outcome before retrying."
                : " Server execution may still be in progress.");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Disconnects the executor and resets the session through <see cref="Bind"/>.</summary>
    /// <remarks>Does not dispose the caller-owned executor or HTTP client.</remarks>
    public void Dispose() => Bind(null);
}
