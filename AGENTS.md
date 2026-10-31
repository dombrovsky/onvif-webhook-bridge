# Maintaining ONVIF Webhook Bridge

## Implementation

- Keep one .NET 10 NativeAOT Minimal API process with DI, independent camera workers, HttpClientFactory, direct SQLite and embedded plain browser files. Keep the diagnostic probe a small standalone console application and retain its shared WS-Addressing renewal adapter.
- Prefer concrete components and minimal changes. Add abstractions only for an actual external seam or test; avoid speculative layers, brokers, ORMs, frontend frameworks and extra services. Preserve the license and unrelated work.
- TaskFlow 1.1.0-rc.9 owns keyed FIFO/round-robin scheduling, four concurrent deliveries, one active job per rule, global/per-rule pending limits of 100/25 and shutdown. Use synchronous non-waiting TrySubmit; do not duplicate its queues, active sets or lifecycle registry.
- Keep rule processing and admission synchronous and atomic with configuration snapshots. History precedes matching. Only accepted jobs consume cooldown. Preserve job identity guards and observe every accepted completion, including queued cancellation. HTTP deadlines/retries remain inside delivery, not enqueue middleware.
- StopAsync must honor the host's cancellation token; final disposal must still settle TaskFlow before its dependencies are disposed. Never abandon accepted work or dispose resources underneath it. Cancellation is cooperative; an uncooperative transport may outlive the container grace period.
- Preserve ordinal topic/text matching, explicit XML booleans, duplicate and structured Source/Data/Key fields, property operations and extension details. Do not assume a field named State, rewrite vendor topics, deduplicate or suppress inspector events.
- Transition rules require a known baseline and stable Source/Key identity; resets, edits, bounded identity eviction and cooldown follow the current [browser guide](docs/BROWSER_ADMINISTRATION.md#matching-and-delivery). Configuration revisions invalidate obsolete work before each request. Isolate camera/action failures and coordinate lease renewal with long-poll timing.
- Inspect the actual pinned library API and version-matched source before changing ONVIF or TaskFlow calls. Do not infer released APIs from upstream main. Preserve bounded bytes/counts, queues, request deadlines, retries and cleanup.

## Security and operation

- Keep tracked files generic: no personal/deployment addresses, device identities, usernames, credentials, real destination URLs, local databases, .env files or unsanitized captures. Use clearly synthetic fixtures and ignored private local configuration. Inspect the diff for secrets before committing.
- Never log URLs, authorization headers, passwords, request/response bodies, SOAP authentication or arbitrary network exception text. Audit library and HttpClientFactory diagnostics; seed fake secrets in redaction tests.
- Fail closed without administrator setup. Authenticate administration/events/manual tests, enforce CSRF/origin checks, escape device content, mask stored secrets and make replacement/clearing explicit.
- Keep TLS verification on by default; retain per-webhook opt-in isolation and strict camera TLS. Do not bypass global certificate, DNS/address, redirect, proxy or self-target restrictions.
- Recoverable secrets and backups require restricted /data ownership and permissions; filesystem restrictions are not encryption at rest. Preserve transaction-before-activation and persistence schemas unless the task requires a reviewed change.
- Use a disposable simulator and local HTTP recorder for tests. Real destination tests cause external actions and need authorization for the actual destination. Coordinate device restarts; never change firmware, detection settings, video/PTZ or destination-host settings incidentally.
- Do not deploy externally, merge, tag, release or publish images unless explicitly authorized. Preserve release/versioning workflows and do not broaden implementation scope.

## Formatting and validation

- Follow .editorconfig. C# control flow uses braces on separate lines and separate statements. Before committing, run `dotnet format whitespace OnvifWebhookBridge.slnx`, then `dotnet format whitespace OnvifWebhookBridge.slnx --verify-no-changes`.
- Run a Release build with analyzers and relevant unit/integration tests; use the full suite for dispatch, security and persistence changes. Fix regressions without weakening assertions. See [development](docs/DEVELOPMENT.md) and the actual [CI workflow](.github/workflows/ci.yml) for commands and coverage.
- Use deterministic synchronization and a controllable clock for timing/recovery tests. A terminal log is not operation quiescence; wait for actual completions/keyed barriers or final flow disposal before asserting settlement or reusing capacity.
- Verify Linux NativeAOT execution for affected ONVIF parsing, JSON, SQLite, authentication and dispatch paths. Investigate AOT/trimming warnings; no blanket suppression. A JIT build alone is insufficient.
- Support linux/amd64 and linux/arm64. Label native execution and cross-compilation/emulation separately; do not promise ARMv7 or Linux 32-bit x86 support.
- Distinguish source review, synthetic fixtures, simulator/recorder tests, native runtime results and real-device interoperability. Verify at least two activation/reset cycles before claiming a real transition mapping works. A 2xx response proves transport acceptance, not destination side effects.
- Report checks actually run and remaining limitations in the PR/handoff. Keep permanent guides current; do not add chronological planning logs or verification diaries.
