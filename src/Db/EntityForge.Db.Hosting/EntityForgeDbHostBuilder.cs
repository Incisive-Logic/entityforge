using EntityForge.Db.Gql;
using EntityForge.Db.Telemetry;
using EntityForge.Db;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using EntityForge.Values;
using EntityForge.Db.Storage;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Http;

namespace EntityForge.Db.Hosting;

/// <summary>Builds one self-hosted EntityForgeDB server using fluent profile and storage configuration.</summary>
public sealed class EntityForgeDbHostBuilder
{
    private readonly WebApplicationBuilder _builder;
    private readonly EntityForgeDbHostOptions _options = new();
    private EntityForgeDbBearerSecurityOptions? _security;
    private bool _built;

    internal EntityForgeDbHostBuilder(string[] args) => _builder = WebApplication.CreateBuilder(args);

    /// <summary>Configures admission, work, result and time limits for query scan endpoints.</summary>
    /// <param name="options">Immutable scan limits retained by the host.</param>
    /// <returns>This builder for further configuration.</returns>
    public EntityForgeDbHostBuilder UseQueryScans(QueryScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options.QueryScans = options;
        return this;
    }

    /// <summary>Stores all database-owned files beneath the specified directory.</summary>
    /// <param name="dataDirectory">The directory to own; relative paths are resolved during host construction.</param>
    /// <returns>This builder for further configuration.</returns>
    /// <exception cref="ArgumentException">The path is empty or whitespace.</exception>
    /// <remarks>Give each running database its own directory. This method does not start recovery.</remarks>
    public EntityForgeDbHostBuilder UseLocalStorage(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _options.DataDirectory = dataDirectory;
        return this;
    }

    /// <summary>Selects the native property-graph profile.</summary>
    public EntityForgeDbHostBuilder UsePropertyGraphProfile()
    {
        _options.Profile = DatabaseProfileId.PropertyGraph;
        return this;
    }

    /// <summary>Registers immutable named read and write procedures for GQL requests.</summary>
    /// <param name="procedures">The validated catalog, retained for this server's lifetime.</param>
    /// <returns>This builder for further configuration.</returns>
    public EntityForgeDbHostBuilder UseGqlProcedures(GqlProcedureCatalog procedures)
    {
        _options.GqlProcedures = procedures ?? throw new ArgumentNullException(nameof(procedures));
        return this;
    }

    /// <summary>Registers independently owned local graphs for standard GQL USE clauses.</summary>
    /// <param name="graphs">The immutable graph-name catalog; requires the property-graph profile.</param>
    /// <returns>This builder for further configuration.</returns>
    public EntityForgeDbHostBuilder UseGqlGraphs(GqlLocalGraphCatalog graphs)
    {
        _options.GqlGraphs = graphs ?? throw new ArgumentNullException(nameof(graphs));
        return this;
    }

    /// <summary>Selects the RDF dataset profile.</summary>
    public EntityForgeDbHostBuilder UseRdfProfile()
    {
        _options.Profile = DatabaseProfileId.Rdf;
        return this;
    }

    /// <summary>Enables bounded SPARQL LOAD for the supplied exact HTTP or HTTPS document URLs.</summary>
    /// <param name="resources">The immutable resource allowlist.</param>
    /// <returns>This builder for further configuration.</returns>
    public EntityForgeDbHostBuilder UseRdfHttpResources(EntityForge.Db.Rdf.RdfSparqlHttpResources resources)
    {
        _options.RdfHttpResources = resources ?? throw new ArgumentNullException(nameof(resources));
        return this;
    }

    /// <summary>Bounds the capacity and idle lifetime of RDF query continuations.</summary>
    /// <param name="options">Immutable paging limits, validated before the database starts.</param>
    /// <returns>This builder for further configuration.</returns>
    public EntityForgeDbHostBuilder UseRdfQueryPaging(EntityForge.Db.Rdf.RdfQueryPagingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options.RdfQueryPaging = options;
        return this;
    }

    /// <summary>Selects the semantics-integrated profile.</summary>
    public EntityForgeDbHostBuilder UseSemanticProfile()
    {
        _options.Profile = DatabaseProfileId.Semantic;
        return this;
    }

    /// <summary>Configures admission limits for atomic structured JSON values.</summary>
    public EntityForgeDbHostBuilder UseJsonAdmissionPolicy(JsonValueAdmissionPolicy policy)
    {
        _options.JsonAdmissionPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <summary>Configures committed-change journal retention.</summary>
    public EntityForgeDbHostBuilder UseChangeJournalRetention(
        DatabaseChangeJournalRetentionOptions retention)
    {
        _options.ChangeJournalRetention = retention ?? throw new ArgumentNullException(nameof(retention));
        return this;
    }

    /// <summary>Configures bounded checkpoint and backup administration.</summary>
    public EntityForgeDbHostBuilder UseAdministration(EntityForgeDbAdministrationOptions options,
        string backupDirectory)
    {
        _options.Administration = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        _options.BackupDirectory = backupDirectory;
        return this;
    }

    /// <summary>Archives and verifies every checkpoint before predecessor WAL history is reclaimed.</summary>
    public EntityForgeDbHostBuilder UseHistoricalEvidenceArchive(string archiveDirectory,
        TimeSpan? retention = null)
    {
        _options.HistoricalArchive = new(archiveDirectory, retention);
        return this;
    }

    /// <summary>Serves a published EntityForge DB Studio browser bundle at <c>/studio</c>.</summary>
    /// <param name="assetsDirectory">Directory containing the published browser index and its complete supporting assets.</param>
    /// <returns>This builder for further configuration.</returns>
    /// <remarks>Publish the WebAssembly host with native linking before configuring these assets.</remarks>
    public EntityForgeDbHostBuilder UseStudio(string assetsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsDirectory);
        _options.StudioAssetsDirectory = assetsDirectory;
        return this;
    }

    /// <summary>Protects every non-discovery HTTP operation with bounded role-hierarchical bearer tokens.</summary>
    public EntityForgeDbHostBuilder UseBearerTokenSecurity(EntityForgeDbBearerSecurityOptions options)
    {
        _security = options ?? throw new ArgumentNullException(nameof(options));
        return this;
    }

    /// <summary>Applies advanced owned-storage and hosting options.</summary>
    public EntityForgeDbHostBuilder ConfigureStorage(Action<EntityForgeDbHostOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_options);
        return this;
    }

    /// <summary>Adds application services to the underlying ASP.NET Core service collection.</summary>
    public EntityForgeDbHostBuilder ConfigureServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_builder.Services);
        return this;
    }

    /// <summary>Configures the underlying ASP.NET Core web host.</summary>
    public EntityForgeDbHostBuilder ConfigureWebHost(Action<IWebHostBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(_builder.WebHost);
        return this;
    }

    /// <summary>Builds the host. A builder can build only one host.</summary>
    /// <returns>A host owned by the caller, which must be started and asynchronously disposed.</returns>
    /// <exception cref="InvalidOperationException">This builder has already been used to build a host.</exception>
    /// <remarks>Building configures services; use the returned host to start recovery and serve requests.</remarks>
    public EntityForgeDbHost Build()
    {
        if (_built)
        {
            throw new InvalidOperationException("An EntityForgeDB host builder can build only once.");
        }

        _built = true;
        EntityForgeDbHostOptions options = _options.Snapshot();
        _builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxRequestBodySize = options.MaximumRequestBodyBytes;
        });
        _builder.Services.AddSingleton(options);
        _builder.Services.AddSingleton<EntityForgeDbTelemetry>();
        if (_security is not null)
        {
            _builder.Services.AddSingleton(_security);
            _builder.Services.TryAddSingleton<IEntityForgeDbAuthorizationPolicy,
                BearerEntityForgeDbAuthorizationPolicy>();
            _builder.Services.TryAddSingleton<IEntityForgeDbSecurityAuditSink,
                LoggingEntityForgeDbSecurityAuditSink>();
        }
        else
        {
            _builder.Services.TryAddSingleton<IEntityForgeDbAuthorizationPolicy,
                DenyEntityForgeDbAuthorizationPolicy>();
        }

        _builder.Services.AddSingleton(options.Administration);
        _builder.Services.AddSingleton<EntityForgeDbAdministrativeJobCoordinator>();
        _builder.Services.AddSingleton<StorageProfileService>();
        _builder.Services.AddSingleton<EntityForgeDbRuntime>();
        _builder.Services.AddSingleton<IGraphQlApplicationServices, HostedGraphQlApplicationServices>();
        _builder.Services.AddSingleton<EntityForgeAdministrationJobs>();
        _builder.Services.AddSingleton<EntityForgeLiveTelemetry>();
        _builder.Services.TryAddSingleton(new EntityForgeObservabilityOptions(Guid.NewGuid().ToString("N")));
        _builder.Services.AddHttpClient<EntityForgeObservability>(client => client.Timeout = TimeSpan.FromSeconds(8))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4 });
        SemanticGraphQlRuntime.Register(_builder.Services);
        ProfileGraphQlRuntime.Register(_builder.Services);
        _builder.Services.AddSingleton(provider => provider.GetRequiredService<EntityForgeDbRuntime>().Applications);
        _builder.Services.AddHostedService(provider => provider.GetRequiredService<EntityForgeDbRuntime>());
        WebApplication application = _builder.Build();
        _ = application.Services.GetRequiredService<EntityForgeLiveTelemetry>();
        if (options.StudioAssetsDirectory is not null)
        {
            var files = new PhysicalFileProvider(options.StudioAssetsDirectory);
            ((IApplicationBuilder)application).Map("/studio", studio =>
            {
                studio.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
                studio.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = files,
                    ServeUnknownFileTypes = true,
                    DefaultContentType = "application/octet-stream",
                    OnPrepareResponse = context =>
                        context.Context.Response.Headers.CacheControl = "no-cache",
                });
                studio.Run(context =>
                {
                    context.Response.Headers.CacheControl = "no-cache";
                    return context.Response.SendFileAsync(
                        Path.Combine(options.StudioAssetsDirectory, "index.html"));
                });
            });
        }
        if (_security is not null)
        {
            application.UseMiddleware<EntityForgeDbSecurityMiddleware>();
        }

        application.UseMiddleware<EntityForgeQueryTracing>();
        application.MapEntityForgeDbEndpoints(options.Profile);
        return new EntityForgeDbHost(application);
    }

}

/// <summary>Provides the supported entry point for building an EntityForgeDB server.</summary>
public static class EntityForgeDbServer
{
    /// <summary>Creates a new host builder using optional command-line arguments.</summary>
    public static EntityForgeDbHostBuilder CreateBuilder(params string[] args) => new(args ?? Array.Empty<string>());
}
