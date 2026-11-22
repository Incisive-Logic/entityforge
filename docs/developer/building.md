# Building from source

## Requirements

- .NET SDK 10.0.404 or a later patch in the same feature band, selected by `global.json`.
- .NET 10 runtime for DB, Studio and tests (included with the SDK).
- ASP.NET Core 10 runtime for framework-dependent server deployments.
- PowerShell 7 for the publishing script.
- The `wasm-tools` workload for browser publishing.

Install the browser workload with:

```powershell
dotnet workload install wasm-tools
```

All libraries, applications and tests target .NET 10. Studio's WebAssembly host uses `net10.0-browser`; the platform suffix selects browser APIs and does not represent a different .NET release. Consuming applications must target .NET 10 or later. Third-party package version numbers are independent of the target framework.

## Build

From the repository root:

```powershell
dotnet build EntityForge.Headless.slnx -c Release -m:1
```

This builds the database and Studio application libraries without compiling the WebAssembly host. With the browser workload installed, build `EntityForge.slnx` to include the browser host.

## Tests

After building:

```powershell
dotnet test tests/Db/EntityForge.Db.Hosting.Tests -c Release --no-build
dotnet test tests/Db/EntityForge.Db.Storage.Pages.Tests -c Release --no-build
dotnet test tests/Studio/EntityForge.Db.Studio.Core.Tests -c Release --no-build
dotnet test tests/Studio/EntityForge.Db.Studio.Avalonia.Tests -c Release --no-build
dotnet test tests/Studio/EntityForge.Db.Studio.Integration.Tests -c Release --no-build
```

## Testing external build artifacts

The architecture, shared-library boundary and release-readiness tests inspect the source checkout as well as the compiled assemblies. When running copied test artifacts or running Windows-built assemblies on Linux, set `ENTITYFORGE_REPOSITORY_ROOT` to the absolute path of the matching checkout on the test machine. It must contain `EntityForge.slnx` and `src/Db`. An invalid explicit path fails the tests rather than selecting another checkout.

For example, in PowerShell from the checkout:

```powershell
$env:ENTITYFORGE_REPOSITORY_ROOT = (Get-Location).Path
dotnet vstest /path/to/artifacts/EntityForge.Db.Hosting.Tests.dll
Remove-Item Env:ENTITYFORGE_REPOSITORY_ROOT
```

On Linux, use the path visible to Linux, including the mounted checkout path when using WSL:

```sh
ENTITYFORGE_REPOSITORY_ROOT=/path/to/entityforge \
  dotnet vstest /path/to/artifacts/EntityForge.Db.Hosting.Tests.dll
```

Without this variable, these tests search ancestors of the working directory, test assembly directory and compile-time source location. The source checkout must match the assemblies being tested; finding a directory does not verify their revision identity. These checks do not replace testing the published server packages on a clean machine.
## Publish server and Studio

For language changes, also run the [query compatibility and release gate](query-compatibility.md).

```powershell
./eng/Publish-Local.ps1
```

The script publishes the server to `artifacts/server` and places browser assets in its `studio` subdirectory. Native WebAssembly linking is required for Skia and HarfBuzz.

Configure an administrator token, persistent data directory and HTTPS endpoint before starting the server. See [hosting](../db/HOSTING.md) and [security](../db/SECURITY.md). Keep credentials and database files outside version control.
