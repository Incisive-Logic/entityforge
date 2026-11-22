# EntityForge DB and Studio

This portable package contains the server, its .NET runtime and the prebuilt browser Studio. No SDK, separate .NET installation or WebAssembly workload is required. Keep all files together.

For a first look, run `./EntityForge.Db.Server demo` on Linux or `.\EntityForge.Db.Server.exe demo` in Windows PowerShell. Open the printed link for a prepared Semantic database, a guided tour and an automatically connected Studio. Follow the [quick start](docs/user/quickstart.md). This is a disposable, local sample; no credentials or import steps are needed.

For your own persistent database, follow [installation and startup](docs/user/deployment.md#portable-windows-and-linux-packages), then the [database guide](docs/user/database.md) and [Studio guide](docs/user/studio.md). Configure a private administrator credential and a persistent data directory outside this installation.

Open `/studio/` on the running server to use Studio. Stop the foreground server with Ctrl+C. This archive does not install a service or start at boot.

`release.json` identifies the build. See [CHANGELOG.md](CHANGELOG.md) for user-facing changes and preview compatibility notes. `licenses/` contains dependency metadata and bundled notices; see also `License.md`, `LICENSING.md`, `THIRD-PARTY-NOTICES.md` and `docs/db/NOTICE`. Dependencies retain their own licences.

`sbom.spdx.json` contains an SPDX 2.3 inventory with hashes of the packaged files, recorded NuGet dependencies and runtime-pack identities. The restore inventory can include build-only packages; it is not a runtime dependency graph or a vulnerability scan. Licence conclusions not established by the inventory are explicitly left unasserted. The SBOM metadata uses CC0-1.0 as required by SPDX; this does not change the licences of the software or bundled notices.

Pre-release APIs and storage formats can change. Back up and test restoration before upgrading; retain the previous package and never run two servers against the same data directory.
