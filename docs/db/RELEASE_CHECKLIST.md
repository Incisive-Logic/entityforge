# Release validation

A release should be reproducible from its source revision. Validate the following before distributing artifacts:

- Build and run the relevant automated tests from a clean checkout.
- Verify server startup, browser Studio and database persistence.
- Test backup, restore and upgrade procedures for the supported release path.
- Check NuGet contents, API XML documentation and dependency versions.
- Confirm the target in [Version.props](../../Version.props), match package/archive/container identities, and prepare the public [changelog](../../CHANGELOG.md). Date its release entry only when publishing; follow the [versioning policy](../developer/versioning.md).
- Verify container configuration, authentication and persistent storage.
- Include component licences and third-party notices in each distribution.
- Verify installation and usage instructions against the artifacts being distributed.
- Verify the [query-language contract](../user/query-languages.md) against executable positive, negative and resource-boundary cases. Check that open-ended traversal never returns an apparently complete bounded prefix.
- Confirm unsupported query/update forms produce structured errors and rejected mutations leave data unchanged. Keep syntax acceptance separate from result correctness and official standards conformance.
- Run the versioned [query compatibility gate](../developer/query-compatibility.md), then exercise both language profiles in extracted Windows and Linux packages, including restart persistence and Studio assets.

See [building from source](../developer/building.md) for build and test commands.
