# Versioning and changelog maintenance

EntityForge DB, Studio and the shared libraries use a coordinated product version.
The root [Version.props](../../Version.props) is its source of truth:
`VersionPrefix` contains the three-part number and `VersionSuffix` contains the
prerelease identifier (or is empty for a stable release).

The current target is **EntityForge 0.8 — Public Preview**, version
`0.8.0-preview.1`. This is a release target, not an announcement that binaries
have already been published.

## Build identity

Run `./eng/Get-Version.ps1` from the repository root to print the version without
building. Normal .NET builds and packs inherit it. The SDK derives numeric assembly
and file versions (`0.8.0.0` for this preview), while NuGet and informational versions
retain the prerelease suffix. Informational versions may also contain the Git commit
identifier. Numeric assembly versions alone do not distinguish preview iterations.

`Build-Portable.ps1`, `Pack-Local.ps1` and `Test-Packages.ps1` use this version by
default. A deliberate `-Version 0.8.0-local.1` override supports local experiments;
use the same override when testing those packages. Direct .NET commands can use
`-p:Version=0.8.0-local.1`. These overrides do not modify the release target.

Portable archive names and `release.json` use the selected version. The container
builder takes its version label from the input package's manifest and defaults its
tag to `entityforge:<package-version>`, so an older archive cannot silently acquire
the current source version. An explicit `-ImageTag` supports another repository or
local alias. The source-build Dockerfile imports the same version definition for
its assemblies; choose its Docker tag explicitly when invoking Docker directly.

Distribution versions omit SemVer build metadata (`+...`) so the same identifier
works as a NuGet version, archive name and Docker tag. Build provenance belongs in
the informational version and release evidence.

## Compatibility during preview

The preview is intended for evaluation and feedback. API or storage changes are
possible before 1.0; a version increment is not a promise that old data directories
can be opened safely. Document breaking changes and migration or export/import
requirements in the changelog. Back up and test restoration before upgrading.
Version 1.0 will represent a defined supported scope and compatibility commitments,
not a claim of complete implementation of every query-language standard.

## Maintain the public changelog

Add noteworthy user-visible changes to [CHANGELOG.md](../../CHANGELOG.md) under
**Unreleased**, using **Added**, **Changed**, **Deprecated**, **Removed**, **Fixed**
or **Security** as appropriate. Omit empty categories. Explain the effect on users,
including breaking behavior and upgrade steps; do not paste raw commit logs or
internal audit/session notes.

When a release is actually published, move its entries into a heading such as
`## [0.8.0-preview.1] - YYYY-MM-DD` with the real release date. Keep a fresh
**Unreleased** section above it. Add release/compare links only when the public
repository and corresponding tags exist; do not invent earlier releases or dates.
Choose the next version in `Version.props` as part of starting the next release.

Building locally never publishes, tags, commits or pushes a release. See
[distribution builds](distribution.md) for package and container commands.
