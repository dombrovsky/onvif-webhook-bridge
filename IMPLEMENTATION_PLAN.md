# Implementation plan

Planning baseline: 2026-10-14; updated after prototype verification. Phase 1 is complete under the revised diagnostic/library-feasibility scope below. Phase 2 is the next coding session; phases 2–8 are not implemented. Each phase remains independently reviewable. Future file names are proposed locations; existing prototype files are listed in phase 1.

## Dependency research

### SharpOnvifClient

[NuGet 0.11.2](https://www.nuget.org/packages/SharpOnvifClient/0.11.2) lists a stable release dated 2026-10-07. Use this pinned baseline; recheck newer stable releases before intentionally changing it. Package metadata and current source are not proof of this application's NativeAOT interoperability.

[Upstream documentation](https://github.com/jimm98y/SharpOnvif) describes generated HttpClient bindings, replacing WCF in 0.10, and both HTTP Digest and WS-UsernameToken authentication. Neither implies a particular camera accepts every scheme.

The inspected [SimpleOnvifClient source](https://raw.githubusercontent.com/jimm98y/SharpOnvif/main/src/SharpOnvifClient/SimpleOnvifClient.cs) exposes:

- `PullPointSubscribeAsync(int initialTerminationTimeInSeconds = 60)` creates an unfiltered request.
- `PullPointPullMessagesAsync(string subscriptionReferenceAddress, int timeoutInSeconds = 60, int maxMessages = 100)` returns `PullMessagesResponse`.
- `PullPointUnsubscribeAsync(string subscriptionReferenceAddress)` exists; there is no PullPoint-renew convenience method in this inspected section. `BasicSubscriptionRenewAsync` uses the generated `SubscriptionManagerClient.RenewAsync` with `RenewRequest`.
- Pulling allows HTTP timeout headroom. Convenience methods shown have no cancellation-token parameter; do not assume cancellation or timeout control that these signatures do not expose.

The initially inspected [client sample](https://raw.githubusercontent.com/jimm98y/SharpOnvif/master/src/Onvif.Client/Program.cs) had cached content predating the transport API. Phase 1 subsequently restored 0.11.2 and reviewed its exact source revision, `a56b8c5f47dc3ce9ba832eb8ebcb1a7eafa5bb71`, including its example and generated contracts. Continue using version-matched APIs, not old cached snippets or motion helpers that discard vendor properties.

Verified implementation detail: SubscriptionManagerClient.RenewAsync accepts a cancellation token, while SimpleOnvifClient convenience calls do not. In 0.11.2 the generated renewal request lacks WS-Addressing headers; the prototype adds them through IClientAuthentication.CreateSecurityHeader and delegates authentication/transport unchanged. Preserve this fix and its regression test when extracting the production adapter. Reference parameters are still not propagated by the convenience methods, and original SOAP-envelope content is not fully exposed.

### .NET, persistence, and containers

[ASP.NET Core NativeAOT guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0) permits Minimal APIs with limitations; static files and health checks are supported, MVC is unsuitable, and authentication choices need scrutiny. Use explicit JSON source-generation metadata. Successful JIT tests are insufficient: run the published native executable with real serialization, HTTP, and later persistence/authentication paths.

[NativeAOT target table](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) lists Linux x64, Arm64 and Arm for .NET 9+. [Cross-compilation guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/cross-compile) requires native toolchains and does not support cross-OS publishing. Plan Linux-native builds; ARMv7 toolchain/base-image availability is still a project-level uncertainty. [.NET 10 OS support](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) lists Linux Arm32/Arm64/x64, not Linux x86. Other runtime architectures do not automatically imply usable AOT/container support. Prefer amd64 and arm64 native runners; emulation is supplemental evidence, not hardware validation.

[Microsoft.Data.Sqlite native-version documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions) describes SQLitePCLRaw and the default bundled native SQLite library. This supports choosing direct SQL without EF Core, but native loading, exact package version, and AOT support must be verified on each image architecture. No SQLite compatibility test has been run.

[Docker's multi-platform Actions guide](https://docs.docker.com/build/ci/github-actions/multi-platform/) supports Buildx and separate platform builds combined into a manifest. It does not establish .NET toolchain or deployment-host compatibility. Health-check tooling must exist in the chosen small runtime image; prefer a tiny built-in health-probe command over adding a large utility solely for probing.

### Device and destination evidence

Library documentation cannot establish behaviour of a particular device or firmware. Phase 1 verified live initialization notifications, lease renewal and clean shutdown on one device; simulation verified repeated/false values, extension preservation and recovery from a pull fault. Complete detection activation/reset cycles and real reboot recovery remain pending. Keep device identity and deployment details in ignored private notes; public evidence uses anonymous labels and sanitized notifications. See [the verification record](docs/ONVIF_OBSERVATIONS.md).

Destinations receive ordinary configured HTTP actions. No vendor API contract is required in the implementation. No live destination has been called; response and action semantics remain unverified.

## Minimal implementation shape

One production project `src/OnvifWebhookBridge/`, one test project `tests/OnvifWebhookBridge.Tests/`, and a temporary focused observation tool `tools/OnvifProbe/`. Keep production components as concrete small classes grouped into `Onvif/`, `Events/`, `Rules/`, `Dispatch/`, `Persistence/`, and `Web/`. Use interfaces only at genuinely testable external seams (camera transport, clock, persistence if needed); no generic repositories, domain framework, internal broker, or plugin abstraction.

Flow: per-camera pull → normalize → bounded inspector history → matcher/state/cooldown → bounded dispatch admission → independent HTTP execution/status. Persistence provides configuration snapshots; it never sits in the polling hot path. Immutable configuration revisions invalidate obsolete queued actions. Use DI, hosted services, HttpClientFactory, direct SQL, and simple polling UI. Add only components required by the current phase.

## Phase 1 — End-to-end ONVIF observation and NativeAOT feasibility

**Status: complete — diagnostic and library feasibility.** Closure uses the focused prototype scope requested after the original plan. It does not claim every original production-interoperability check passed. Remaining checks are assigned below rather than silently marked verified.

**Dependencies:** .NET 10 SDK, pinned SharpOnvifClient 0.11.2, private endpoint/optional credentials; real-device access for observation.

**Completed scope:** one-camera console probe using actual SimpleOnvifClient APIs; service discovery; unfiltered PullPoint subscribe/pull; complete exposed message-tree/raw XML output; renewal with WS-Addressing compatibility fix; delayed reconnect/resubscribe; Ctrl+C and bounded unsubscribe/disposal. No forwarding, UI, database or production multi-camera system.

**Delivered files:** `tools/OnvifProbe/{OnvifProbe.csproj,Program.cs}`, `tests/OnvifProbe.Smoke.ps1`, `README.md`, `.gitignore`, and `docs/ONVIF_OBSERVATIONS.md`. Private launch profiles/configuration remain ignored.

**Acceptance evidence:**

- Build: .NET SDK 10.0.400, `net10.0`, zero warnings/errors; exact restored library API/source reviewed.
- Live device: connect, unfiltered subscribe, receive initialization notifications with topics/fields/timestamps/operations, reproduce original renewal fault, then verify at least 25 successful corrected renewals over more than 40 minutes without recurrence. Ctrl+C unsubscribed and stopped cleanly.
- Simulation: repeated true and false states, duplicate fields, Key/structured/unknown XML, raw message output, unique renewal MessageIDs, and resubscription after a separate pull fault passed.
- NativeAOT/trimming: user reports successful verification with the library. Exact target/publish arguments/runtime paths were not supplied; preserve this attribution rather than claim agent-run Linux verification.

**Carried-forward verification:** phase 2 confirms activation/reset semantics before validating a real transition rule; phase 4 tests real restart/network recovery and independent workers; phase 8 verifies Linux/container architectures. Other-device reference-parameter support remains a known interoperability limitation to address when encountered. Broader malformed-payload/normalization coverage belongs in phase 2. No vendor schema or endpoint is hardcoded based on this feasibility result.

**Handoff:** the library choice and basic event path are proven enough to proceed. Preserve the observer as a diagnostic tool, reuse its proven subscription/renewal approach, and avoid another transport rewrite or package upgrade without a concrete reason.

## Phase 2 — Normalized events and deterministic rule matching

**Status: next focused coding session.** Goal: turn observed notifications into testable, vendor-independent rule decisions. HTTP actions remain phase 3 so matching can be verified without triggering external services.

**Dependencies:** completed phase 1 probe, 0.11.2 notification contract and WS-Addressing fix, REQUIREMENTS R2/R3. Synthetic fixtures may drive development; a real mapping requires privately observed activation/reset evidence. No dependence on an assumed boolean field name.

**Exact scope:** create the small production event/matching component and focused tests. Normalize directly from the library-exposed XmlElement instead of relying on detection helpers. Add a 100-event bounded history and exact camera/topic/Source-or-Data matching, explicit boolean mode, occurrence/transition policy and cooldown/admission state. Use in-memory test rules and recorded decisions only. Keep the working probe available; no HTTP dispatch, web server, DI framework, persistence, multiple workers or deployment files in this session.

**Expected files/components:** `src/OnvifWebhookBridge/OnvifWebhookBridge.csproj`, `Events/{NormalizedEvent,EventNormalizer,EventHistory}.cs`, `Rules/{ForwardingRule,RuleMatcher,RuleState}.cs`, `tests/OnvifWebhookBridge.Tests/` project with focused fixtures/tests, and updates to the verification record. Add a controllable time seam only if required for timing tests. No generic repository or automation framework. Actual device configuration/captures stay private; committed fixtures use synthetic topics/fields.

**Acceptance criteria:**

- Preserve full topic text, dialect and exposed namespace context; both timestamps, property operation, Source/Data/Key collections including duplicates, structured fields and complete available raw message XML. Document library loss versus application truncation separately. Exact matching uses the displayed topic string, including any prefix; do not silently strip prefixes or implement semantic topic rewriting in this phase.
- Admit every normalized notification to bounded history before matching; false/unmatched/Initialized messages remain visible. Unknown/malformed optional content does not discard the raw message or break processing.
- Multiple independent enabled rules may match one event. Match a configured section/name/value; text equality is ordinal and boolean mode recognizes XML lexical true/false/1/0. Missing fields do not match; duplicate entries follow REQUIREMENTS R3.
- In transition mode the first/Initialized observation establishes a baseline, not a trigger. Only a known nonmatching-to-matching change fires; false/nonmatching observations update state during cooldown. Repeated matching values do not retrigger. Deleted/epoch-reset/rule-edit handling, identity bounds and atomic cooldown admission follow R3.
- Before declaring a **real transition mapping** verified, privately observe at least two activation/clear cycles for each event type used by that mapping. Validate field section/value, stable Source/Key identity and property-operation behaviour. If a device emits no reset or only initialization, document that and prefer occurrence mode; do not invent transitions. Generic matcher tests can pass while this specific check remains pending.
- Existing diagnostic smoke test remains green. No camera configuration changes and no external actions are required.

**Tests/verification:** synthetic fixtures cover namespace-prefixed topics and differing aliases (exact string misses are intentional), case/topic misses, Source/Data name collision, duplicate/missing fields, missing/invalid timestamp, unknown operations, structured extensions and malformed optional content. Test independent rules/identities, first/false→true/true→true/true→false, Initialized/Deleted/reconnect/rule-edit resets, XML boolean versus text mode, cooldown boundaries with a fake monotonic clock, history count/byte caps, identity eviction and simultaneous admission. No sleeps in timing tests. Recheck NativeAOT/trimming on the new normalization/matching path and record the target/results without personal details.

**Exit/handoff:** deliver normalized events plus deterministic matching/admission decisions and passing tests. Record any pending device-cycle check explicitly. Phase 3 then attaches generic bounded HTTP delivery to admitted matches; it must not send a live transition action until that mapping's real lifecycle is verified.

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
