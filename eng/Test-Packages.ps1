param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [string]$Version = '',
    [Parameter(Mandatory = $true)][string]$WorkDirectory
)
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-Version.ps1" -Version $Version
$packages = [IO.Path]::GetFullPath($PackageDirectory)
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path $work) { throw 'Use a fresh consumer work directory to avoid cached package or database state.' }
New-Item -ItemType Directory -Path $work | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$inventory = @{}
foreach ($file in Get-ChildItem $packages -Filter "*.$Version.nupkg") {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entry = $zip.Entries | Where-Object FullName -like '*.nuspec' | Select-Object -First 1
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $metadata = $spec.package.metadata
        if ($metadata.version -ne $Version) { throw "Unexpected package version: $($file.Name)" }
        if ($metadata.license.InnerText -ne 'Apache-2.0') { throw "Unexpected licence metadata: $($file.Name)" }
        $names = @($zip.Entries.FullName)
        foreach ($required in @('License.md', 'THIRD-PARTY-NOTICES.md')) {
            if ($required -notin $names) { throw "Missing $required in $($file.Name)" }
        }
        if ($metadata.id -eq 'EntityForge.Db.Query.Syntax') {
            foreach ($required in @('licenses/opengql/LICENSE.txt', 'licenses/opengql/NOTICE.txt')) {
                if ($required -notin $names) { throw "Missing grammar attribution $required in $($file.Name)" }
            }
        }
        if ($metadata.id -eq 'EntityForge.Db.Gql' -and 'licenses/Unicode-LICENSE.txt' -notin $names) {
            throw "Missing Unicode attribution in $($file.Name)"
        }
        foreach ($dll in $names | Where-Object { $_ -match '^lib/.*\.dll$' }) {
            if ($dll -notmatch '^lib/net10\.0/') { throw "Unexpected target framework in $dll" }
            if ([IO.Path]::ChangeExtension($dll, '.xml') -notin $names) { throw "Missing API XML for $dll" }
        }
        $inventory[[string]$metadata.id] = @($spec.SelectNodes("//*[local-name()='dependency']") | ForEach-Object { @{id=$_.id;version=$_.version} })
    }
    finally { $zip.Dispose() }
}
if (!$inventory.Count) { throw 'No packages matched the requested version.' }
foreach ($id in $inventory.Keys) {
    foreach ($dependency in $inventory[$id]) {
        if ($dependency.id.StartsWith('EntityForge.')) {
            if (!$inventory.ContainsKey($dependency.id) -or $dependency.version -notlike "*$Version*") {
                throw "Incomplete or inconsistent package closure: $id -> $($dependency.id) $($dependency.version)"
            }
        }
    }
}
$inventory | ConvertTo-Json -Depth 5 | Set-Content "$work/package-inventory.json"
'<Project />' | Set-Content "$work/Directory.Build.props"
'<Project />' | Set-Content "$work/Directory.Build.targets"
'<Project />' | Set-Content "$work/Directory.Packages.props"
$escapedPackages = [Security.SecurityElement]::Escape($packages)
@"
<configuration>
  <packageSources><clear /><add key="local" value="$escapedPackages" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local"><package pattern="EntityForge.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content "$work/NuGet.Config"
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="EntityForge.Db.Graph" Version="[$Version]" />
    <PackageReference Include="EntityForge.Db.Semantic" Version="[$Version]" />
    <PackageReference Include="EntityForge.Db.Rdf" Version="[$Version]" />
    <PackageReference Include="EntityForge.Db.Hosting" Version="[$Version]" />
    <PackageReference Include="EntityForge.Db.Distributed.Client" Version="[$Version]" />
    <PackageReference Include="EntityForge.Db.Distributed.Hosting" Version="[$Version]" />
  </ItemGroup>
</Project>
"@ | Set-Content "$work/Consumer.csproj"
@'
using EntityForge.Db;
using EntityForge.Db.Graph;
using EntityForge.Db.Graph.Contracts;
using EntityForge.Db.Hosting;
using EntityForge.Db.Client;
using EntityForge.Db.Rdf;
using EntityForge.Db.Rdf.Contracts;
using EntityForge.Db.Semantic;
using EntityForge.Db.Mapping;
using Microsoft.AspNetCore.Hosting;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var boundaries = new (Type Type, string Assembly)[]
{
    (typeof(EntityForge.Db.Gql.GqlProcedureCatalog), "EntityForge.Db.Gql"),
    (typeof(EntityForge.Db.Distributed.Sharding.LogicalShardId), "EntityForge.Db.Distributed.Contracts"),
    (typeof(EntityForge.Db.Distributed.Hosting.HttpPartitionEntityClient), "EntityForge.Db.Distributed.Client"),
    (typeof(EntityForge.Db.Distributed.Hosting.PartitionDatabaseNode), "EntityForge.Db.Distributed.Hosting")
};
foreach (var boundary in boundaries)
{
    if (boundary.Type.Assembly.GetName().Name != boundary.Assembly)
        throw new InvalidOperationException("Unexpected package assembly: " + boundary.Type.FullName);
}

var data = Path.Combine(AppContext.BaseDirectory, "consumer-data-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data);
try
{
    var graphPath = Path.Combine(data, "graph");
    var vertex = GraphVertexId.New();
    var payload = new DatabaseValue.String(new string('g', 128 * 1024));
    await using (var database = await GraphDatabase.OpenLocalAsync(new(graphPath)))
    {
        await database.CommitAsync(new(Guid.NewGuid(),
            [new CreateVertex(vertex, ["PackageCheck"]), new SetVertexProperty(vertex, "payload", payload)]));
        await database.CreateCheckpointAsync();
    }
    await using (var reopened = await GraphDatabase.OpenLocalAsync(new(graphPath)))
    {
        if (reopened.GetVertex(vertex)?.Properties["payload"] != payload)
            throw new InvalidOperationException("Graph package recovery lost the large property.");
    }

    var rdfPath = Path.Combine(data, "rdf");
    var literal = new RdfLiteral(new string('r', 128 * 1024));
    await using (var database = await RdfDatabase.OpenLocalAsync(new(rdfPath)))
    {
        await database.CommitAsync(new(Guid.NewGuid(),
            [new AssertRdfQuad(new(new RdfIri("urn:package:subject"), new("urn:package:payload"), literal))]));
        await database.CommitSparqlUpdateAsync(Guid.NewGuid(),
            "INSERT DATA { GRAPH <urn:package:graph> { <urn:package:s> <urn:package:p> 42 } }");
        await database.CreateCheckpointAsync();
    }
    await using (var reopened = await RdfDatabase.OpenLocalAsync(new(rdfPath)))
    {
        var result = (RdfSparqlSelectResult)await reopened.QuerySparqlAsync(
            "SELECT ?value { <urn:package:subject> <urn:package:payload> ?value }");
        if (result.Bindings.Count != 1 || result.Bindings[0]["value"] != literal)
            throw new InvalidOperationException("RDF package recovery lost the large literal.");
        var named = (RdfSparqlSelectResult)await reopened.QuerySparqlAsync(
            "SELECT ?value { GRAPH <urn:package:graph> { <urn:package:s> <urn:package:p> ?value } FILTER(?value = 42) }");
        if (named.Bindings.Count != 1)
            throw new InvalidOperationException("Packaged SPARQL Update/query failed after recovery.");
    }

    // Verify Semantic mapped writes, restart persistence and both read-only native views
    // from the package closure, including rejection without read credentials.
    var semanticToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    for (int pass = 0; pass < 2; pass++)
    {
        await using (var semantic = await SemanticDatabase.OpenLocalAsync(new(Path.Combine(data, "hosted-semantic"))))
        {
            var store = semantic.EntityStore<SemanticPackageItem>();
            if (pass == 0) await store.SaveAsync(new("one", "package-semantic"));
            if (await store.FindAsync("one") is not { Name: "package-semantic" })
                throw new InvalidOperationException("Semantic package restart lost a mapped value.");
        }
        await using var semanticHost = EntityForgeDbServer.CreateBuilder()
            .UseLocalStorage(Path.Combine(data, "hosted-semantic"))
            .UseBearerTokenSecurity(new(semanticToken, requireHttps: false))
            .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0")).Build();
        await semanticHost.StartAsync();
        try
        {

            using var semanticHttp = new HttpClient { BaseAddress = new Uri(semanticHost.Application.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
            using var deniedSemantic = await semanticHttp.GetAsync("/sparql?query=ASK%20%7B%7D");
            if (deniedSemantic.StatusCode != HttpStatusCode.Unauthorized && deniedSemantic.StatusCode != HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Semantic native view accepted an unauthenticated query.");
            semanticHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", semanticToken);
            using var sparql = await semanticHttp.GetAsync("/sparql?query=" + Uri.EscapeDataString("SELECT ?value WHERE { ?s ?p ?value FILTER(?value = \"package-semantic\") }"));
            sparql.EnsureSuccessStatusCode();
            using var sparqlJson = JsonDocument.Parse(await sparql.Content.ReadAsStringAsync());
            var bindings = sparqlJson.RootElement.GetProperty("results").GetProperty("bindings");
            if (bindings.GetArrayLength() != 1 || bindings[0].GetProperty("value").GetProperty("value").GetString() != "package-semantic")
                throw new InvalidOperationException("Packaged Semantic SPARQL returned an incorrect result.");
            using var gql = await semanticHttp.PostAsJsonAsync("/v1/semantic/gql/query", new { query = "MATCH (v) WHERE v.scalar = 'package-semantic' RETURN v.scalar AS answer" });
            gql.EnsureSuccessStatusCode();
            using var gqlJson = JsonDocument.Parse(await gql.Content.ReadAsStringAsync());
            var matches = gqlJson.RootElement.GetProperty("matches");
            if (matches.GetArrayLength() != 1 || matches[0].GetProperty("bindings").GetProperty("answer").GetString() != "package-semantic")
                throw new InvalidOperationException("Packaged Semantic GQL returned an incorrect result.");
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await semanticHost.StopAsync(stop.Token);
        }
    }

    // Exercise the hosting and GQL parser dependency closure through its public HTTP boundary.
    // All data, credentials and the loopback listener belong to this temporary consumer.
    var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    await using var host = EntityForgeDbServer.CreateBuilder()
        .UseLocalStorage(Path.Combine(data, "hosted-graph"))
        .UsePropertyGraphProfile()
        .UseBearerTokenSecurity(new(token, requireHttps: false))
        .ConfigureWebHost(web => web.UseUrls("http://127.0.0.1:0"))
        .Build();
    await host.StartAsync();
    try
    {
        using var hostedClient = new HttpClient
        {
            BaseAddress = new Uri(host.Application.Urls.Single()),
            Timeout = TimeSpan.FromSeconds(30)
        };
        using var denied = await hostedClient.PostAsJsonAsync("/v1/graph/gql/query", new { query = "RETURN 42 AS answer" });
        if (denied.StatusCode != HttpStatusCode.Unauthorized && denied.StatusCode != HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Packaged host accepted an unauthenticated query.");
        hostedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var inserted = await hostedClient.PostAsJsonAsync("/v1/graph/gql/update",
            new { transactionId = Guid.NewGuid(), statement = "INSERT (:Person {amount: 42})" });
        if (!inserted.IsSuccessStatusCode)
            throw new InvalidOperationException("Packaged GQL update failed: " + await inserted.Content.ReadAsStringAsync());
        using var response = await hostedClient.PostAsJsonAsync("/v1/graph/gql/query",
            new { query = "MATCH (n:Person) RETURN n.amount AS answer" });
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Packaged GQL query failed: " + await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var matches = json.RootElement.GetProperty("matches");
        if (matches.GetArrayLength() != 1 || matches[0].GetProperty("bindings").GetProperty("answer").GetInt32() != 42)
            throw new InvalidOperationException("Packaged GQL query returned an unexpected result.");
    }
    finally
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StopAsync(stop.Token);
    }
}
finally
{
    Directory.Delete(data, recursive: true);
}
using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8080/") };
var client = new EntityForgeClient(http);
if (client.Host is null) throw new InvalidOperationException("Client composition failed.");
Console.WriteLine("Package-only consumer verified Graph/RDF writes, large-value recovery, SPARQL query/update, authenticated GQL query/update, Semantic mapped restart/native reads and HTTP client composition.");

[EntityType("package-check", "semantic-item")]
public sealed record SemanticPackageItem(string Id, string Name);
'@ | Set-Content "$work/Program.cs"
& dotnet restore "$work/Consumer.csproj" --configfile "$work/NuGet.Config" --packages "$work/nuget-cache"
if ($LASTEXITCODE -ne 0) { throw 'Independent package restore failed.' }
& dotnet run --project "$work/Consumer.csproj" -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Independent package consumer failed.' }
Write-Host "Validated $($inventory.Count) packages, their EntityForge closure, XML files and source notices."
Write-Host 'Third-party runtime/native/font notice completeness requires a separate distribution review.'
