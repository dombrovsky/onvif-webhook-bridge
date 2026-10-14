# Implementation plan

Planning baseline: 2026-10-14. No code, camera access test, package restore, build, or runtime verification was performed in this session. Each numbered phase is a separate focused coding session, with explicit inputs and an independently reviewable completion record. File names below are intended locations, not files already present.

## Dependency research

### SharpOnvifClient

[NuGet 0.11.2](https://www.nuget.org/packages/SharpOnvifClient/0.11.2) lists a stable release dated 2026-10-07. Use this pinned baseline; recheck newer stable releases before intentionally changing it. Package metadata and current source are not proof of this application's NativeAOT interoperability.

[Upstream documentation](https://github.com/jimm98y/SharpOnvif) describes generated HttpClient bindings, replacing WCF in 0.10, and both HTTP Digest and WS-UsernameToken authentication. Neither implies a particular camera accepts every scheme.

The inspected [SimpleOnvifClient source](https://raw.githubusercontent.com/jimm98y/SharpOnvif/main/src/SharpOnvifClient/SimpleOnvifClient.cs) exposes:

- `PullPointSubscribeAsync(int initialTerminationTimeInSeconds = 60)` creates an unfiltered request.
- `PullPointPullMessagesAsync(string subscriptionReferenceAddress, int timeoutInSeconds = 60, int maxMessages = 100)` returns `PullMessagesResponse`.
- `PullPointUnsubscribeAsync(string subscriptionReferenceAddress)` exists; there is no PullPoint-renew convenience method in this inspected section. `BasicSubscriptionRenewAsync` uses the generated `SubscriptionManagerClient.RenewAsync` with `RenewRequest`.
- Pulling allows HTTP timeout headroom. Convenience methods shown have no cancellation-token parameter; do not assume cancellation or timeout control that these signatures do not expose.

The inspected [client sample](https://raw.githubusercontent.com/jimm98y/SharpOnvif/master/src/Onvif.Client/Program.cs) loops over `messages.NotificationMessage`, using only motion/tamper helpers. Its cached content predates the current transport API. Treat it as evidence of sample flow, not copy-ready version-matched code. Do not use helpers that discard vendor properties. In phase 1 inspect the **restored 0.11.2 assembly and matching source**, including generated Events clients, serialization, cancellation, renewal, endpoint reference headers, and extensions. Record version/source revision and compile against actual signatures. A lower-level generated client may be needed; keep that decision inside one ONVIF adapter.

### .NET, persistence, and containers

[ASP.NET Core NativeAOT guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0) permits Minimal APIs with limitations; static files and health checks are supported, MVC is unsuitable, and authentication choices need scrutiny. Use explicit JSON source-generation metadata. Successful JIT tests are insufficient: run the published native executable with real serialization, HTTP, and later persistence/authentication paths.

[NativeAOT target table](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) lists Linux x64, Arm64 and Arm for .NET 9+. [Cross-compilation guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile) requires native toolchains and does not support cross-OS publishing. Plan Linux-native builds; ARMv7 toolchain/base-image availability is still a project-level uncertainty. [.NET 10 OS support](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) lists Linux Arm32/Arm64/x64, not Linux x86. Other runtime architectures do not automatically imply usable AOT/container support. Prefer amd64 and arm64 native runners; emulation is supplemental evidence, not hardware validation.

[Microsoft.Data.Sqlite native-version documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions) describes SQLitePCLRaw and the default bundled native SQLite library. This supports choosing direct SQL without EF Core, but native loading, exact package version, and AOT support must be verified on each image architecture. No SQLite compatibility test has been run.

[Docker's multi-platform Actions guide](https://docs.docker.com/build/ci/github-actions/multi-platform/) supports Buildx and separate platform builds combined into a manifest. It does not establish .NET toolchain or deployment-host compatibility. Health-check tooling must exist in the chosen small runtime image; prefer a tiny built-in health-probe command over adding a large utility solely for probing.

### Device and destination evidence

Library documentation cannot establish behaviour of a particular device or firmware. Event availability, topic/field names, authentication, initialization/reset notifications, lease timing and recovery require direct observation. No device-specific behaviour was verified in this planning session. Keep device identity and deployment details in ignored private notes; public evidence uses anonymous labels and sanitized notifications.

Destinations receive ordinary configured HTTP actions. No vendor API contract is required in the implementation. No live destination has been called; response and action semantics remain unverified.

## Minimal implementation shape

One production project `src/OnvifWebhookBridge/`, one test project `tests/OnvifWebhookBridge.Tests/`, and a temporary focused observation tool `tools/OnvifProbe/`. Keep production components as concrete small classes grouped into `Onvif/`, `Events/`, `Rules/`, `Dispatch/`, `Persistence/`, and `Web/`. Use interfaces only at genuinely testable external seams (camera transport, clock, persistence if needed); no generic repositories, domain framework, internal broker, or plugin abstraction.

Flow: per-camera pull → normalize → bounded inspector history → matcher/state/cooldown → bounded dispatch admission → independent HTTP execution/status. Persistence provides configuration snapshots; it never sits in the polling hot path. Immutable configuration revisions invalidate obsolete queued actions. Use DI, hosted services, HttpClientFactory, direct SQL, and simple polling UI. Add only components required by the current phase.

## Phase 1 — End-to-end ONVIF observation and NativeAOT feasibility

**Dependencies:** none beyond the existing repository and .NET 10/native build environment. Real verification needs camera access, credentials if required, and someone able to generate detections/reboot the camera. Do not run upstream sample PTZ/video operations.

**Exact scope:** build a one-camera observation tool using the actual SharpOnvifClient 0.11.2 APIs. Read device/services/event properties when supported; establish an unfiltered PullPoint subscription; pull and inspect all notification properties/unknown XML; unsubscribe. Exercise lease renewal/recreation and transport recovery sufficiently to prove the path. No rules, webhook actions, UI, DB, production multi-camera management, or deployment files.

**Expected files:** `tools/OnvifProbe/{OnvifProbe.csproj,Program.cs}`, minimal parser/adapter only where reusable, `tests/OnvifWebhookBridge.Tests/` parser fixtures/tests, `docs/ONVIF_OBSERVATIONS.md`, `docs/fixtures/onvif/` sanitized captures, solution/build settings and `.gitignore` as needed. No checked-in credentials, auth envelopes, raw secret subscription tokens, or unsanitized captures.

**Acceptance criteria:**

- Compile against pinned package; record exact API/types used, transport/serialization limitations, and evidence labels: source-reviewed, simulated, real-camera, native-runtime.
- Real run at a privately configured endpoint observes available event types separately, their true/false or actual alternative lifecycle, full topics, Source/Data/Key, ElementItems, timestamps and property operations. Repeat activation/clear at least twice. If any signal is absent, report absent/unknown rather than assigning an assumed schema.
- Keep device model/firmware/settings and real endpoint details in ignored private notes. Public evidence uses anonymous labels and records sanitized subscription shape, requested/granted lease, idle long-poll behaviour, timeout relationship, renewal beyond two lease periods, and recovery after a coordinated restart/network interruption. Confirm no accidental topic filter.
- Publish NativeAOT for an initial Linux target and run subscribe/parse/recovery in that native executable. Investigate warnings rather than suppressing them globally. If AOT fails, document a narrow remediation or explicit stack decision; do not silently replace NativeAOT or the library.
- Demonstrate bounded shutdown and preservation of extension data. Determine how to carry endpoint reference parameters and cancel underlying calls with actual supported APIs.

**Tests/verification:** sanitized representative SOAP fixtures include human/motion examples explicitly labelled synthetic until captured, true/false, unknown topic, duplicates, Source/Data/Key, missing timestamp, ElementItem, malformed payload and namespace aliases. Verify normalization loses no available properties. Separate native smoke-test evidence from JIT tests and source review.

**Exit gate:** if hardware is inaccessible, deliver reproducible probe instructions and simulated parser/native results, mark **real-camera gate pending**, and stop camera-specific implementation assumptions. Later generic phases may be explicitly requested while this gate is pending, but must not claim device interoperability or select an unobserved mapping. If hardware is reachable, confirm observed event behaviour before proceeding to the remainder. Each failure has a concrete recorded next step.

## Phase 2 — Normalized events and deterministic rule matching

**Dependencies:** phase 1 parser contract, topic representation, identity/property-operation findings; completed real-camera gate for the normal sequence.

**Exact scope:** promote the proven parser into the production project; implement bounded event history, exact topic/Source-or-Data matching, explicit boolean mode, occurrence/transition policy and cooldown/admission state. No network dispatch, persistence, UI or multiple workers.

**Expected files:** `Events/{NormalizedEvent,EventNormalizer,EventHistory}.cs`, `Rules/{ForwardingRule,RuleMatcher,RuleState}.cs`, corresponding focused tests and updated observation/semantics documentation.

**Acceptance criteria:** all notification properties survive normalization; display/matching topic relationship is explicit; multiple rules can match; false/unmatched messages remain in history and update state; first/Initialized observations do not trigger transitions; repeated true does not retrigger until reset; cooldown and bounded identity eviction follow REQUIREMENTS R3. Match a configured field, never a guessed `State` field.

**Tests/verification:** exact case/topic misses; Source/Data same name; duplicates/missing values; XML boolean lexical values versus text; independent rules/identities; first/false→true/true→true/true→false; Initialized/Deleted/unknown operation; reconnect/rule reset; cooldown boundary with fake monotonic time; history count/byte caps; simultaneous admission and overflow rollback. No sleeps in timing tests.

## Phase 3 — Single-camera generic HTTP forwarding

**Dependencies:** phases 1–2.

**Exact scope:** connect the proven one-camera pipeline to HttpClientFactory dispatch with an in-memory rule configuration, bounded queue/concurrency, manual test service and execution status. No UI or SQLite yet; private test configuration stays ignored.

**Expected files:** `Dispatch/{HttpActionDispatcher,DispatchQueue,ExecutionStatus}.cs`, minimal `Program.cs`/DI wiring and single-camera hosted worker using the proven adapter, `tests/.../Dispatch/`, `docs/HTTP_VERIFICATION.md`.

**Acceptance criteria:** observed or simulated event reaches a local HTTP recorder with configured GET or POST, headers and literal JSON; two rules both dispatch independently; delivery failure leaves polling responsive. Validate URLs/headers/body; redirects disabled, limits/cancellation/deadline/retry opt-in enforced, logs redacted. Manual test clearly reports transport result without claiming destination-side effect.

**Tests/verification:** fake handler plus local receiver covers request construction, headers isolated across destinations, response disposal, 2xx/4xx/5xx/408/429, Retry-After cap, timeout/transport cancellation, zero/two additional attempts, duplicate-effect caveat, overflow, per-rule serialization and fairness. Capture logs containing deliberately seeded fake secrets and assert absence. With a privately configured destination, verify a real matching notification causes the expected destination action, separately from HTTP status; otherwise record this check pending. Label every simulated run.

## Phase 4 — Multiple independent cameras and resilient lifecycle

**Dependencies:** phases 1–3; reuse rather than rewrite the proven subscription path.

**Exact scope:** extend single-worker wiring to a camera supervisor with one task/client/subscription per enabled camera and runtime configuration reconciliation. Harden renewal, backoff, status and graceful stop. Still in-memory configuration, no UI/DB.

**Expected files:** `Onvif/{CameraConfiguration,CameraSupervisor,CameraSubscriptionWorker,CameraStatus}.cs`, adapter updates only as required, lifecycle tests.

**Acceptance criteria:** two cameras subscribe independently; one outage/reboot does not interrupt the other or dispatch; enable/edit/disable/remove cancel obsolete work and invalidate obsolete jobs; reconnect refreshes discovered services and resets transition epochs; lease renew/recreate and empty polls are correct; clients do not accumulate across resubscriptions. Stop completes within a documented deadline.

**Tests/verification:** deterministic fake transport for two cameras, expired lease, authentication/unsupported-service faults, network drop, restart, invalid subscription, renewal failure, repeated empty polls, backoff cap/jitter, edit racing a pull, shutdown, unsubscribe failure and leaked-worker/client checks. Real camera outage/recovery run if available; a second simulated camera is acceptable but labelled. Repeat native smoke test for lifecycle changes.

## Phase 5 — SQLite configuration persistence

**Dependencies:** phases 2–4 configuration and reconciliation contracts.

**Exact scope:** persist cameras/rules/schema and administrator bootstrap credential hash with direct SQL, transactions and masked secret DTOs. Add startup load and migration handling; no browser UI. Validate SQLite in an AOT executable now rather than waiting for packaging.

**Expected files:** `Persistence/{ConfigurationStore,SchemaMigrator}.cs`, `Persistence/Migrations/`, configuration validation and secret-safe DTOs, persistence tests, `docs/DATA_STORAGE.md`.

**Acceptance criteria:** restart restores enabled configurations, secrets and rules; transient event/dispatch state is not persisted; parameterized writes are atomic; referenced-camera deletion rejected; failed save leaves active snapshot unchanged; permissions cover DB and sidecars. Unreadable/corrupt/unwritable data fails clearly without exposing secrets or deleting data. Exact SQLite dependency/native asset versions recorded.

**Tests/verification:** temporary DB round-trip, migrations from an earlier schema, rollback, foreign keys, simultaneous edits/revision conflict, explicit secret preserve/replace/clear, invalid configuration, restart reconciliation, backup/restore and AOT native SQLite loading. Verify actual Linux file modes; Windows ACL behaviour is not evidence for container modes.

## Phase 6 — Authenticated Minimal API administration

**Dependencies:** phases 3–5.

**Exact scope:** administrator environment bootstrap/reset policy, authentication/session/CSRF/login throttling, camera/rule CRUD/status/event/test APIs, source-generated JSON and safe validation. No browser screens yet. Do not use unsupported auth APIs merely because they work under JIT.

**Expected files:** `Web/{AdminAuthentication,ApiEndpoints,ApiModels,JsonContext}.cs`, `Program.cs` wiring, API/security integration tests and administrator setup documentation.

**Acceptance criteria:** fail closed without admin; no plaintext admin password persisted; all administration and test routes authenticated; mutations/manual tests reject missing/invalid CSRF or foreign origin; secret reads masked; conflict/validation results understandable; liveness reveals no configuration. Environment bootstrap cannot silently override an established administrator. Native publish/run covers login, persistence and API serialization.

**Tests/verification:** unauthenticated/expired session, correct/incorrect login and throttling, secure cookie options, CSRF/origin checks, restart invalidation, CRUD revision handling, masked secrets and exception/log redaction. Test full camera→event→rule→local HTTP path via authenticated APIs, including worker reconciliation.

## Phase 7 — Minimal browser screens

**Dependencies:** phase 6 and earlier pipeline.

**Exact scope:** plain static HTML/CSS/JavaScript for Cameras, Events and Rules; login/logout; lightweight refresh; rule-from-event draft; test actions and safe feedback. No bundler or frontend package manager.

**Expected files:** `wwwroot/{index.html,app.css,app.js}` or three small page files, browser verification notes and only focused security/flow tests where useful.

**Acceptance criteria:** all R5 screen operations work through protected APIs; inspector shows both states and structured/additional properties without silent suppression; rule draft uses observed matching string and chosen section/value and starts disabled; last HTTP status/error is visible; save/edit secrets are masked and unambiguous. Camera/XML/URL content is rendered as text, not executable markup. Refresh polling is bounded and stops at logout.

**Tests/verification:** browser walkthrough CRUD/test/connectivity/disconnected status, multiple matching rules, false/unmatched/Initialized display, create/edit draft and secret clear, server restart, login/session expiry; injected HTML/script in event/name fields stays inert. Repeat native full-path smoke test, not just a development server preview.

## Phase 8 — Docker, Compose, multi-platform CI and release guide

**Dependencies:** phases 1–7; NativeAOT/SQLite/auth verification already available.

**Exact scope:** production multi-stage NativeAOT image, non-root permissions, health probe, normal bridge deployment, configurable port/data, build/test/publish CI and README. No additional integrations or feature expansion.

**Expected files:** `Dockerfile`, `.dockerignore`, `docker-compose.yml`, `.env.example` without secrets, `.github/workflows/{ci.yml,images.yml}`, `README.md`, `docs/DEPLOYMENT_VERIFICATION.md`; tiny health-probe command in production entry point if needed.

**Acceptance criteria:** amd64/arm64 images run tested native executables; evaluate arm/v7 build/toolchain/native SQLite and hardware smoke test, adding it to manifests only after passing. Explicitly explain no Linux x86 support. Native per-platform build jobs feed a manifest; PRs build/test without registry write credentials, trusted releases can publish GHCR with minimal permissions. Pin reviewed dependencies/actions/base images and document upgrades.

**Tests/verification:** `docker compose config`, image build per target, UID/file permissions, mounted data persistence after recreation, read-only root filesystem if adopted, configurable port and localhost default bind, working health probe, graceful termination/restart, DB migration/restore, TLS CA trust, and camera/event/subscription/destination access over bridge networking. Do not mark camera outages as process health failures. Check image manifest, startup/idle memory/CPU/image size, queue overload and longer two-camera run. Verify on target hardware if available; otherwise explicitly record deployment-host interoperability pending. Publish only generic platform specifications and anonymous test labels.

README must explain how to create mappings from observed notifications, synthetic versus hardware results, environment/password-file bootstrap, HTTPS proxy, volume ownership/backups, connection troubleshooting, cooldown/transitions/retries and possible duplicate actions. HTTP walkthroughs use generic GET/POST rules, synthetic topics/fields and clearly labelled placeholders, never personal addresses, device models, deployment details, credentials or real destination URLs. Finish with tests and platform limitations recorded.

## Session handoff and completion rules

For each phase, record scope completed, files changed, commands/results, real versus simulated verification, package/runtime versions, open issues, and the next phase's inputs. Commit only the scoped phase; do not mark acceptance items passed without evidence. Missing hardware means pending hardware checks, not successful interoperability. An actual dependency incompatibility requires updating this plan with a concrete option and decision before broadening the stack.
