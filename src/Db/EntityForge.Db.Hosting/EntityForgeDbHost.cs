using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

namespace EntityForge.Db.Hosting;

/// <summary>Owns a configured EntityForgeDB web application and its database runtime.</summary>
public sealed class EntityForgeDbHost : IAsyncDisposable
{
    internal EntityForgeDbHost(WebApplication application) => Application = application;

    /// <summary>Gets the underlying ASP.NET Core application for endpoint and middleware customization.</summary>
    public WebApplication Application
    {
        get;
    }

    /// <summary>Starts the host and database runtime.</summary>
    /// <param name="cancellationToken">Cancels the host operation.</param>
    /// <returns>A task representing host startup.</returns>
    public Task StartAsync(CancellationToken cancellationToken = default) => Application.StartAsync(cancellationToken);

    /// <summary>Gracefully stops the host.</summary>
    /// <param name="cancellationToken">Bounds the graceful shutdown wait.</param>
    /// <returns>A task representing graceful shutdown.</returns>
    public Task StopAsync(CancellationToken cancellationToken = default) => Application.StopAsync(cancellationToken);

    /// <summary>Runs the host until cancellation or shutdown.</summary>
    /// <param name="cancellationToken">Cancels the host operation.</param>
    /// <returns>A task representing the running host lifetime.</returns>
    public Task RunAsync(CancellationToken cancellationToken = default) =>
        ((IHost)Application).RunAsync(cancellationToken);

    /// <summary>Disposes the web application and its owned database services.</summary>
    /// <returns>A task that completes when owned host resources have been disposed.</returns>
    public ValueTask DisposeAsync() => Application.DisposeAsync();
}
