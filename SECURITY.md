# Security

ONVIF Webhook Bridge is intended for operator-controlled networks. Administration, configuration, event inspection and manual tests require an authenticated administrator. There are no default credentials. Use the loopback bind default and [validated HTTPS](docs/DEPLOYMENT.md#https-and-trusted-certificates) for remote access. The application does not trust forwarded headers.

## Protecting your installation

- Restrict the administrator password file and persistent data volume. The administrator password is stored as a salted adaptive hash, but camera credentials and action secrets in SQLite and backups are recoverable plaintext protected by filesystem permissions, **not encryption at rest**. See [storage and backup](docs/DATA_STORAGE.md).
- Treat event payloads and HTTP response previews as sensitive. Visible rule URLs are disclosed to authenticated administrators; mark token-bearing URLs sensitive. Credentials, headers, bodies and sensitive URLs are write-only through configuration reads.
- Only configure cameras and destinations you trust. Administrators can intentionally reach private network endpoints. Address checks reject several unsafe target classes, but do not identify every alias of the application or replace network isolation. See [network guidance](docs/DEPLOYMENT.md#network-diagnosis-and-mappings).
- TLS certificate validation is enabled by default. The per-rule **Ignore certificate validation** option weakens HTTPS protection for that action and its manual tests; camera TLS remains strict. Prefer installing the correct CA certificates.
- Manual tests cause real external actions. Optional retries can duplicate effects. Restrict administrator access accordingly.

There is one administrator account and no multi-user/RBAC model. Keep deployments current; no supported-version or security-response-time commitment has been established for a public release.

## Reporting a vulnerability

Do not disclose an unpatched vulnerability, credentials or private captures in a public issue. If the repository's **Security → Report a vulnerability** option is available, use it for a private report. GitHub [supports private vulnerability reporting for public repositories](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository). While this repository remains private, it cannot be enabled here; enable and test it as part of making the repository public. Until a private channel is available, open an issue asking the maintainer for a private reporting method without including exploit or sensitive details.

Provide affected revisions, a sanitized description and reproduction steps through the agreed private channel. Never submit a real configuration database, password file or unsanitized SOAP capture.
