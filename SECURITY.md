# Security policy

EntityForge 0.8 is preview software. Evaluate it within a controlled network
boundary and review the [security model and deployment requirements](docs/db/SECURITY.md).
The preview does not carry a long-term support commitment. Security fixes will be
identified in the [changelog](CHANGELOG.md); use the latest published preview and
review its upgrade notes.

## Report a vulnerability privately

Use this repository's **Security → Report a vulnerability** flow:
[private vulnerability report](https://github.com/Incisive-Logic/entityforge-db/security/advisories/new).
Do not put exploit details, credentials, private data or database backups in public
issues or discussions.

If GitHub does not offer the private reporting button, do not disclose the issue
publicly. Open an issue requesting a private security contact, without technical
details, and wait for a private channel before sharing the report.

Include the affected version, operating system, deployment/profile, impact and a
minimal reproduction using synthetic data. Maintainers will assess the report and
coordinate a fix and disclosure. No guaranteed response time is currently offered.

Automated tests and dependency scans do not constitute an independent security
review. See the [documented trust boundaries](docs/db/SECURITY.md#threat-model-and-trust-boundaries)
before using the server with sensitive data or untrusted clients.
