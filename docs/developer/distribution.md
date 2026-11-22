# Local distribution builds

EntityForge distribution builds produce profile/client NuGet libraries, portable server/Studio archives and a server/Studio container. Component boundaries do not imply a single aggregate `EntityForge.Db` NuGet package. Choose the [client or embedded entrypoint](../db/PACKAGE_TOPOLOGY.md) appropriate to the application; NuGet resolves its implementation dependencies.

Versions default to the root [Version.props](../../Version.props). See [versioning and changelog maintenance](versioning.md) for overrides and preview compatibility.

## Build packages without uploading

### Portable server and Studio

For portable server archives, use PowerShell 7 with the pinned SDK and verified `wasm-tools`:

```powershell
./eng/Build-Portable.ps1 -OutputDirectory ./artifacts/portable-preview
```

Use a new output directory for every run; previous builds are never overwritten. The script creates a self-contained Windows x64 ZIP, Linux x64 tar.gz and `SHA256SUMS`. Each archive contains the server, runtime, linked Studio assets, guides, changelog, licences, dependency inventory and `release.json` in one versioned folder. Build intermediates remain beside the archives for inspection; distribute only the archives and checksum file. No upload or service registration occurs.

Each portable package also includes `sbom.spdx.json`, generated after staging and before archiving. It records SHA-256 and SHA-1 file checksums, the SPDX package verification code, recorded NuGet identities and runtime-pack identities. Dependency records are metadata-only: the complete restore inventory includes build tools, and the generator does not infer runtime dependency edges or map every published file to a source package. Unverified licence conclusions use `NOASSERTION`; existing notices remain authoritative. SPDX's CC0-1.0 metadata licence does not relicense the payload. This inventory is not a vulnerability scan or a container base-image SBOM.

`release.json` records the SDK, the server's included .NET and ASP.NET Core frameworks, and the resolved runtime-pack identities, including Studio's WebAssembly runtime. Review these actual bundled versions when checking security updates: the SDK version alone does not identify every runtime in a distribution. Replacing the machine's installed runtime does not patch a self-contained archive. The framework identities are read from the published server's runtime configuration; they are not inferred from the build machine's installed runtimes.

Run `./eng/Test-PortableSbom.ps1` to check generation, file hashes, relationships, licence handling and overwrite refusal. Pass `-SchemaPath <spdx-2.3-schema.json>` to also validate against the [official SPDX 2.3 JSON schema](https://github.com/spdx/spdx-spec/blob/v2.3/schemas/spdx-schema.json). To inspect an existing staged package without changing it, use `./eng/New-PortableSbom.ps1 -PackageDirectory <stage> -OutputPath <new-external-file>`. The destination must not exist. Regenerate the SBOM whenever package contents change, before producing archive checksums.

The browser is built once, with linker warnings treated as errors. Each server RID uses separate intermediate output and the server is not trimmed. Linux executable permissions are set explicitly, including when packaging on Windows. Extract and test each archive on its target OS (WSL can validate Linux): verify readiness, authentication, queries, persistence after restart, and the actual Studio UI. Cross-publishing alone does not validate startup. Review dependency notices before distribution. Runtime security updates require rebuilding these self-contained packages. See [portable startup](../user/deployment.md#portable-windows-and-linux-packages).

To reuse build intermediates between sequential packaging runs, pass `-BuildArtifactsDirectory ./artifacts/portable-cache`. Keep the output directory new for each candidate; the cache contains mutable compilation intermediates and must not be shared by concurrent builds. Studio publishes into the new candidate directory so older fingerprinted browser assets cannot accumulate in the distribution. Archive retention is separate from the reusable cache.

### NuGet libraries


From the root in PowerShell 7:

```powershell
./eng/Pack-Local.ps1 -ArtifactsPath ./artifacts/package-validation
./eng/Test-Packages.ps1 -PackageDirectory ./artifacts/package-validation/packages -WorkDirectory ./artifacts/package-consumer
```

The script packs DB libraries and their shared runtime dependencies with the same central version (or an explicitly supplied local override). It excludes server applications, prototypes, tests and UI components. Output is a local package directory; the script does not upload anything. Some packages are implementation details rather than supported application entrypoints.

Test packages in a separate consumer project with exact versions and the local package directory added as a restore source. That consumer must use PackageReference, not ProjectReference. Keep nuget.org available for third-party dependencies. Check that every EntityForge dependency resolves from the same local package set and that public libraries include XML documentation. Building against source alone does not verify this distribution boundary.

The repository URL defaults to `https://github.com/Incisive-Logic/entityforge-db` through `Version.props`; forks can override `EntityForgeRepositoryUrl`. Review licence files, copyright notices, dependency versions, runtime/native/font assets and package contents before distributing them.

`Test-Packages.ps1` uses an isolated package cache and a fresh consumer directory.
It checks package dependency closure, licence metadata, notices and API XML, then
executes Graph and RDF writes, checkpoint/reopen checks with 128 KiB values, and
SPARQL update/query checks against the restored RDF state. It also starts a
temporary authenticated loopback host, rejects an unauthenticated query, and
checks a GQL insert/query result through the packaged hosting and parser libraries.
It also verifies mapped Semantic writes and restart recovery through the public embedded API, then checks authenticated GQL and SPARQL reads against the same data through a temporary host.
The consumer removes
its owned database fixtures after execution; its build outputs and package cache
remain in the work directory for inspection. This is a package smoke check, not
full language or production qualification. Run the resulting consumer on each
supported OS as part of distribution validation.

## Container

Build a release image from the extracted Linux portable package:

```powershell
./eng/Build-Container.ps1 -PackageDirectory ./artifacts/portable-preview/entityforge-0.8.0-preview.1-linux-x64
```

The package directory is the build context. It supplies the same self-contained server, Studio assets, documentation and dependency notices as the portable distribution. The script validates the package manifest and required files, builds for Linux x64, and does not push the image. The container uses the .NET runtime-dependencies base image and runs as its non-root `app` user (UID 1654). It listens on port 8080 and stores data in `/var/lib/entityforge`; supply credentials and choose the profile at startup.

Record the resulting image ID and base-image digest with release evidence. Rebuild both the portable package and image when bundled runtime dependencies require updates. The separate source-build Dockerfile remains available for development through [the deployment guide](../user/deployment.md).

The container script uses `--pull --no-cache` and installs available OS package updates so an old cached layer does not hide newly released fixes. This requires network access. Scan the final image, including its bundled runtime and OS packages, and retain the scanner version, advisory-database date and exact image digest. A NuGet restore audit alone does not cover the container's OS packages or prove that a self-contained runtime is patched.

Validate authentication, readiness, browser startup, persistent-volume ownership and backup/restore with the actual image. A successful server build does not validate WebAssembly assets or container startup.

## Documentation

```powershell
./eng/Build-Docs.ps1
```

The pinned local DocFX tool builds HTML into `artifacts/docs/site`. Public/protected declarations and XML comments produce the API reference for the packaged DB libraries, shared dependencies and application model. This includes advanced storage, durability, routing and query-planning APIs as well as the client and embedded database APIs. The syntax and storage-composition assemblies currently expose no public reference pages. Generated files are ignored; edit source comments and guides instead.

Reference inclusion describes the available declarations; it does not give infrastructure APIs the same compatibility guarantees as the supported client and embedded entrypoints, or qualify distributed deployments for production. Start with the [public API guide](../db/PUBLIC_API.md) when choosing an integration surface.

The API output is **HTML, not generated Markdown**. Edit XML comments beside the C# declarations for API descriptions and edit the Markdown guides for tutorials and workflows. Local NuGet packaging includes the compiler-produced XML files for IDE help. There is no Markdown API export step.

The documentation build supplements its staged XML with explicitly labelled descriptions of compiler-generated record members and Protocol Buffers wire mechanics. It checks those members against the compiled assemblies and leaves undocumented handwritten methods as gaps. This step never rewrites authored comments, generated sources or package XML. Small external XML supplements document third-party helpers without changing their source or licence. Generated wire-field descriptions identify serialization fields; use the authored codec and client contracts for application-level semantics.

Run `./eng/Test-ApiDocumentation.ps1` after generation to measure summary coverage. Add `-RequireComplete` to reject undocumented symbols. This measures the presence of summaries, not their accuracy or the completeness of parameter and exception descriptions.

Test the supplementation rules with `./eng/Test-ApiDocumentationSupplements.ps1 -ArtifactsPath <scratch-directory>`. The fixture checks preservation of authored comments, generated member coverage, retention of handwritten documentation gaps and repeatability. Its temporary files are removed after the test. Documentation regeneration replaces the generated API and site directories so obsolete pages cannot survive assembly or namespace changes.

Serve the output with `dotnet tool run docfx serve artifacts/docs/site` from the repository root to use navigation and search. No GitHub remote is required.

For public methods, document purpose, parameters, return values, cancellation, observable exceptions and ownership/retry guarantees. Include examples for entrypoints. An API page without a meaningful summary remains a documentation gap, even when its signature renders. Keep the generated reference and handwritten workflows together.
