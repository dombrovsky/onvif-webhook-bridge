# Development

This is the shared project reference for contributors, maintainers and coding agents.
For installation use [deployment](DEPLOYMENT.md); for behavior visible in the browser
use the [user guide](BROWSER_ADMINISTRATION.md). Authentication/API details live in
[administration](ADMINISTRATION.md), persistence in [data storage](DATA_STORAGE.md),
and versioning/publication in [releasing](RELEASING.md).

## Contents

- [Getting started and contribution workflow](#contribution-workflow)
- [Architecture](#architecture-and-repository-structure) and [implementation contracts](#implementation-contracts)
- [Local container builds](#local-container-builds) and [testing and validation](#validation)
- [Diagnostic probe](#diagnostic-probe)
- [Compatibility and verification limitations](#compatibility-and-evidence), including [screenshot maintenance](#screenshots)

## Architecture and repository structure

One .NET 10/C# 14 NativeAOT process hosts Minimal APIs, embedded plain HTML/CSS/JavaScript,
independent ONVIF PullPoint workers, matching/dispatch through TaskFlow 1.1.0-rc.10 and SQLite configuration.
There is no frontend build, ORM, message broker or external database. Keep abstractions
at actual external seams; avoid speculative frameworks or vendor-specific core logic.
The product excludes video/RTSP, MQTT, automation engines, plugins and multi-user/RBAC.

| Path | Responsibility |
| --- | --- |
| `src/OnvifWebhookBridge/Program.cs` | CLI flags, storage/admin startup, API host |
| `src/OnvifWebhookBridge/Onvif/` | Independent subscriptions, supervision, timing, shared renewal adapter |
| `src/OnvifWebhookBridge/Events/` | Immutable normalization snapshots and bounded history |
| `src/OnvifWebhookBridge/Rules/` | Exact matching, transition identity, atomic cooldown/admission |
| `src/OnvifWebhookBridge/Dispatch/` | Configuration revisions, bounded fair queue, HTTP delivery/results |
| `src/OnvifWebhookBridge/Persistence/` | Direct SQL store, migrations, private file contract |
| `src/OnvifWebhookBridge/Web/` and `wwwroot/` | Sessions, API DTOs/source-generated JSON, embedded browser assets |
| `src/OnvifProbe/` | Standalone diagnostic console; links only shared renewal-adapter source |
| `tests/OnvifWebhookBridge.Tests/` | xUnit unit, storage/security and Testcontainers integration tests |
| `tests/OnvifWebhookBridge.NativeSmoke/` | Test-only Linux NativeAOT runner with shared acceptance paths |
| `tests/*.ps1`, `tests/*.cjs`, `tests/test_release.py` | Probe, native, browser, packaging and release verification |
| `build/release.py`, `.github/workflows/` | Version validation, CI and tested-image publishing |

Flow: camera pull → normalize → history → match/state/cooldown → queue admission →
TaskFlow scheduling → independent HTTP execution/status. Configuration is validated and committed before
active snapshots change. Persistence is outside the event/dispatch hot path.

## Local container builds

Public installations use the [published-image Quick start](../README.md#quick-start). To build changed source locally, clone this repository, enter its directory and run:

```sh
docker build --platform linux/amd64 -t onvif-webhook-bridge:local .
```

Use `linux/arm64` on an ARM64 builder. After the same password-volume initialization described in deployment, set `ONVIF_IMAGE=onvif-webhook-bridge:local` in ignored `.env`, then `docker compose up -d --no-build`. The default Compose file never builds; rebuild explicitly after source changes. Do not run `compose pull` for your local-only tag. See [validation](#validation) for NativeAOT and platform tests.

Optional release-style OCI labels use the actual source version and revision:

```sh
docker build --build-arg VERSION="$(dotnet msbuild src/OnvifWebhookBridge/OnvifWebhookBridge.csproj -getProperty:Version)" --build-arg REVISION="$(git rev-parse HEAD)" -t onvif-webhook-bridge:local .
docker run --rm --network none onvif-webhook-bridge:local --version
```

`VERSION` must match MSBuild's value; it never overrides the compiled version. Ordinary local builds without labels remain supported. Release CI supplies and verifies both labels.

## Contribution workflow

Clone [the repository](https://github.com/dombrovsky/onvif-webhook-bridge), create a
focused branch and open a PR describing the resulting behavior and validation.
Discuss substantial additions in an issue first. Follow `.editorconfig`, retain
warnings as errors, use source-generated JSON for AOT-facing types, and investigate
AOT/trimming warnings rather than broadly suppressing them. C# uses braces on
separate lines and separate statements.

Update relevant shared documentation with contract changes. Do not keep session
handoffs, completed-task diaries or a parallel agent reference. Preserve tests for
observable behavior and failure boundaries; change/remove coverage only when its
contract changes or equivalent coverage replaces it. Never skip or weaken tests
just to hide a failure. Report unavailable prerequisites and checks not run.

Keep repository examples and fixtures synthetic and vendor-independent. Never commit
real endpoints, credentials, device identities, databases or unsanitized captures.
Use ignored `.local/`, `.env` and `*.local.*` files. Inspect the final diff for scope,
links, whitespace and secrets. Publishing/merging/tagging/deploying is a separate
explicitly authorized operation; contribution validation does not authorize it.

## Implementation contracts

### Event preservation and bounds

The displayed `Topic` is the exact matching string, including prefixes, whitespace
and case. Do not strip namespace aliases. Preserve exposed dialect/namespace context,
local receipt and camera timestamp text/parse, property operation and ordered
Source/Data/Key collections. Preserve duplicate names, missing versus empty values,
ElementItems and extensions. Only ONVIF-schema scalar SimpleItems satisfy conditions;
vendor lookalikes and structured fields remain inspection data. No boolean field
name or detection topic is assumed. Snapshots must not retain mutable source DOMs.

Every processed notification enters history before matching/admission, including
false, unmatched, Initialized, Deleted, disabled and truncated observations. There
is no deduplication. Raw XML is serialized library-exposed DOM content, not a complete
SOAP envelope or original wire bytes. Library-discarded data cannot be recovered.

| Resource | Bound |
| --- | --- |
| Configuration | 64 cameras, 256 rules |
| History | 100 events and 1 MiB variable payload globally; oldest evicted |
| Normalized event | 64 KiB UTF-8 variable payload, 16 KiB per string/XML preview |
| Event collections | 256 section items total; up to 256 topic nodes and 256 topic attributes |
| Transition cache | 1,024 identities globally, FIFO eviction; reads do not refresh order |
| ONVIF response | 1 MiB buffered SOAP; requests ask for at most 100 messages per pull |
| Queue | 100 pending globally, 25 per rule; four active globally, one per rule |
| Action | URL 8,192 UTF-16 units; 32 headers / 16 KiB combined UTF-8; POST JSON 64 KiB / depth 64 |
| HTTP result | Response headers 32 KiB; final JSON preview 16 KiB / depth 32 |

Managed-object overhead is additional to variable-payload budgets. Strings clip at
Unicode scalar boundaries; XML previews can become incomplete XML. Truncation is
explicit and prevents all admissions from that event, clearing the affected camera's
transition baselines. The next complete observation establishes a fresh baseline.
Transport bounds limit incoming allocation separately from post-parse retention.
Matching strings and rule names are capped at 4,096 UTF-16 units; API request bounds
are documented in [administration](ADMINISTRATION.md#routes-and-edits).

### Matching and admission

Camera/topic, selected Source/Data field name and text value use ordinal,
case-sensitive equality. Missing values do not match; duplicate scalar fields match
if any selected value equals the condition. Boolean mode accepts exactly `true`/`1`
and `false`/`0`. Topic-only rules and independent multiple matches are supported.

Occurrence mode admits each match subject to cooldown. Transition mode requires a
condition: first/Initialized establishes baseline, Deleted clears identity, and only
a known nonmatching-to-matching change fires. Unknown property operations use ordinary
matching. Nonmatching observations update baselines even during cooldown.

Transition identity is partitioned by rule/camera/topic and complete Source/Key
collections, excluding the selected scalar Source condition field. Length framing
prevents collisions; ordinal sorting ignores collection order but preserves
multiplicity, missing/empty values and attributes. Extra attributes sort by expanded
name; structured content uses retained XML without semantic prefix rewriting.
Without Source/Key fields the camera/topic partition is the identity. FIFO eviction
returns an identity to unknown-baseline semantics.

A lock protects snapshots, state, cooldown and synchronous queue admission. Admission
callbacks must be bounded and non-reentrant. Cooldown uses monotonic time, is per rule
across identities and starts only on accepted work; failure after acceptance consumes
it. Rejection consumes no cooldown but state has already advanced, so rejected or
suppressed transitions are not replayed. Rule changes/removal/enable changes reset
state/cooldown; equal configurations retain them. A successful new subscription
resets only that camera's transition identities and preserves cooldown. Restart
clears all transient state. Do not silently add deduplication or replay.

### Workers and configuration revisions

`CameraSupervisor` coalesces edits through a capacity-one signal, rereads the latest
snapshot, cancels obsolete workers and joins cleanup before starting replacements.
There must never be two active workers for one camera. Unchanged cameras continue
independently. Any camera edit, including its name, invalidates its jobs; old pulls
remain in history but cannot update matching state or admit work. Removal/recreation
must not revive old jobs or pulls. Eligibility/revision checks run before attempts
and immediately before sends. Already-sent requests cannot be recalled.

Each worker discovers services, subscribes without filters and polls sequentially.
It requests a 120-second lease; returned camera-relative times define a monotonic
budget charged from request start. Renewal starts at 30 seconds remaining, bounded
by ten seconds and the remaining lease. Pulls allow up to 20 seconds with 20 seconds
reserved from the lease; request time is at most poll duration plus ten seconds and
never exceeds the remaining lease. Unusable leases trigger resubscription.
Immediate responses are paced by 250 ms; empty polls are successful contacts.
Reconnect backoff grows from 0.75–1 second to 22.5–30 seconds with jitter; 30 seconds
of successful contact resets failures. Error after three failures remains recoverable.

The pinned SharpOnvifClient 0.11.2 convenience calls lack cancellation parameters.
Bound waits, dispose transports and observe abandoned faults. Renewal uses the
generated cancellable API and shared `AddressedAuthentication` adapter, which adds
Action, unique MessageID, To and anonymous ReplyTo without replacing authentication.
Keep the addressing/probe regressions: missing MessageID can cause renewal faults.
The version-matched upstream source is
[a56b8c5f47dc3ce9ba832eb8ebcb1a7eafa5bb71](https://github.com/jimm98y/SharpOnvif/tree/a56b8c5f47dc3ce9ba832eb8ebcb1a7eafa5bb71).
Production fails closed for subscription reference parameters that address-only
convenience calls cannot propagate. Do not imply general endpoint-reference support.

Shutdown aborts polling, uses a fresh transport for up to three seconds of best-effort
unsubscribe, and targets five seconds including scheduling. Cleanup runs concurrently
across cameras. A worker retains one failed cleanup address and retries it once after
recovery discovery before subscribing again. An unreachable remote subscription may
survive until lease expiry. Compose allows 15 seconds for process termination.

### HTTP delivery and diagnostics

TaskFlow 1.1.0-rc.10 owns keyed FIFO/round-robin scheduling, capacity and lifetime.
Use its published package and version-matched source; do not duplicate queues,
active sets or the lifecycle registry in application code. Production binds the
singleton dispatcher's synchronous, non-waiting `TrySubmit` to `CameraPipeline`;
configuration-only validation supplies an explicit rejecting callback without
allocating a flow. Immutable revisions accompany each accepted job.

Overflow rejects the newest job. FIFO within a rule and round-robin scheduling
across ready rules prevent a burst from monopolizing execution. Selected work
remains pending until its delivery delegate actually starts. Retries hold that
rule's active slot. Manual tests
require an enabled camera, permit a disabled rule and bypass matching/cooldown,
while sharing queue, revision and transport limits.

Attempts have ten-second deadlines; jobs expire 30 seconds after admission, including
queue time and retry delays. Zero extra attempts is default, with up to two opt-in.
Retry transport/timeouts, 408, 429 and 5xx, with jitter or Retry-After only if the delay
fits the remaining job deadline. Redirects/ordinary 4xx are not retried. No durable
outbox, replay or exactly-once guarantee exists; even GET can cause duplicate effects.

Actions permit only HTTP/HTTPS GET/POST, no embedded credentials, fragments or control
characters. Headers are validated, detached per request, and cannot duplicate names
or override transport/content-controlled headers. JSON is literal, without templates.
Redirects, proxies and cookie sharing are disabled; DNS is checked once and sockets
connect to those checked addresses. Private endpoints remain allowed; see
[network policy and limitations](DEPLOYMENT.md#network-diagnosis-and-mappings).
TLS is strict by default; an explicit per-action exemption uses a separate named
HttpClientFactory pool and never changes camera transport or other restrictions.

Observe every accepted completion, including queued cancellation. Cancellation
winning before admission produces a cancelled rejection rather than overflow.
Current status belongs to the latest admission per rule; an older active job cannot
overwrite it. Last-completed execution can belong to an older admission that finished
later. Preserve the job identity guards when publishing results.

Shutdown closes admission and cancels queued/active work. `StopAsync` honors the
host cancellation token, but final DI disposal must still settle the original
TaskFlow before its HTTP dependencies are disposed. Cancellation is cooperative:
a transport ignoring it can outlive graceful shutdown and be killed at Docker's
hard deadline, without guaranteed status publication or destination effects.

Tests reusing capacity or pausing execution must wait for actual TaskFlow completion
or quiescence. Terminal logging occurs before the delegate returns. Use same-key,
no-HTTP barriers, actual completions/final flow disposal and deterministic transport,
cancellation or fake-clock timer gates; do not infer settlement from a log or sleep.

ResponseHeadersRead and bounded reads prevent unbounded previews. Only JSON media
types (`application/json` or `+json`) are retained; empty/non-JSON/invalid/oversized/
unreadable responses give fixed diagnostics. Preview failure preserves the HTTP
outcome and cannot itself trigger a retry. A later transport failure clears earlier
response diagnostics. Latest admission/current status and last completed execution
are separate maps bounded by configured rules, removed with those rules.

Response previews and event payloads are sensitive administrator-only transient data.
Never persist/log them. Production clears logging providers, removes factory request
loggers and leaves the library logger unset. Safe delivery diagnostics contain only
IDs, fixed categories, attempts, status and duration. No URLs (visible or sensitive),
credentials, headers, bodies or arbitrary exception messages belong in logs.
Security and transaction contracts continue in [administration](ADMINISTRATION.md)
and [storage](DATA_STORAGE.md); do not bypass the store for persistent mutations.

Optional metrics-only OTLP export, instrument names, privacy filters and configuration are described in [metrics](METRICS.md). Its synthetic export and shutdown checks also run in the NativeAOT smoke runner.

## Validation

Install the .NET 10 SDK; [CI](../.github/workflows/ci.yml) defines the SDK version
used for verification. From the repository root:

```sh
dotnet restore OnvifWebhookBridge.slnx
dotnet build OnvifWebhookBridge.slnx -c Release --no-restore
dotnet test OnvifWebhookBridge.slnx -c Release --no-build --filter Category=Unit
python3 -m unittest discover -s tests -p 'test_release.py' -v
```

Docker-free unit/storage/security tests use synthetic values and temporary databases;
local HTTP listeners are still required. Docker integration requires PowerShell 7,
a running Linux Docker engine and the pinned simulator image:

```powershell
./tests/Build-OnvifSimulator.ps1
dotnet test OnvifWebhookBridge.slnx -c Release --no-build
dotnet build src/OnvifProbe/OnvifProbe.csproj
./tests/OnvifProbe.Smoke.ps1
./tests/Verify-Phase7Native.ps1 -Platform linux/amd64
```

Use [onvifsim](https://github.com/mrtian2016/onvifsim/tree/8f86706c2ba768006e85b38f6bc1c760d36f1bba)
at the revision pinned in the build script. Tests use its actual control API to create
ONVIF notifications; end-to-end delivery uses local HTTP recorders. Do not substitute
hand-built SOAP/events in integration tests. Unit tests may use fake clocks/transports
and synthetic fixtures. Simulator requests are buffered because its control API does
not accept chunked bodies; scenarios supply synthetic authentication explicitly.
Missing Docker/image prerequisites fail visibly rather than silently skipping tests.
The probe's synthetic SOAP smoke force-terminates its child, so it does not verify Ctrl+C.

Native verification publishes both production and test-only executables and exercises
SQLite loading/migrations/permissions, bootstrap/reset, API/session/CSRF/JSON, embedded
assets, strict/opt-in TLS, real simulator delivery, persistence/restart and two-camera
lease/fault/recovery/cleanup. `-StorageOnly` omits simulator paths. Historical phase
runner filenames remain compatibility entry points; use `Verify-Phase7Native.ps1`.
It stages explicit source allowlists under ignored `.local/` and removes disposable
containers. No private configuration belongs in build/test contexts. A JIT build
alone cannot establish NativeAOT correctness.

Use `-Platform linux/arm64` on native ARM64. An amd64 host can use
`-Platform linux/arm64 -CrossCompile` with configured emulation; report that as
cross-compilation/emulated execution. Do not claim native hardware coverage from it.

For browser regression, first build the amd64 native fixture above. Install Node and
an external Playwright module/browser (test tooling only), using the version and
installation commands in [CI's browser tooling step](../.github/workflows/ci.yml).

Then in PowerShell 7:

```powershell
$env:ONVIF_BROWSER_CHANNEL = 'chromium'
./tests/Verify-Phase7Browser.ps1 -PlaywrightModule "$PWD/.local/browser/node_modules/playwright"
```

Default runs use embedded native assets. `ONVIF_BROWSER_SOURCE_ASSETS=1` is an explicit
development override, not verification of the packaged assets. Separate browser
response fixtures test rendering/expiry for data onvifsim cannot emit; they are not
protocol evidence. Automated phone viewports do not prove physical touch, mobile
Safari or screen-reader compatibility. Browser-controlled password-save prompts
require separate manual checks.

For production packaging, after building the native fixture and simulator:

```powershell
docker build --platform linux/amd64 -t onvif-webhook-bridge:local .
./tests/Verify-Deployment.ps1 -Image onvif-webhook-bridge:local -Platform linux/amd64 -SoakSeconds 600
./tests/Verify-Compose.ps1 -Image onvif-webhook-bridge:local
./tests/Verify-InboundTls.ps1 -Image onvif-webhook-bridge:local -Platform linux/amd64
./tests/Verify-Workflows.ps1
./tests/Verify-ImageIndex.ps1 -Build
```

Published-image smoke checks use `tests/Verify-DockerRun.ps1 -Pull` and
`tests/Verify-Compose.ps1 -Pull` (PowerShell 7 and Docker). They require `latest` to
exist; they do not build or publish images. The Docker-run check extracts the README
startup command. Compose verification uses an isolated YAML-only directory, a private
password volume, health/UI/login, disabled synthetic camera persistence and changed
password reset. Both preserve operator configuration. Optional `-PlaywrightModule`
adds browser verification; see the script parameters for version/digest assertions.
CI runs candidate-image checks, without requiring a public release for a PR.

For the packaged browser walkthrough also pass `-PlaywrightModule` to deployment
verification and set `ONVIF_BROWSER_CHANNEL` as above. Repeat on each supported
platform with separate image tags; label emulation. Tests remove their own fixture
containers/projects/volumes; caches/images and ignored staging may remain. Never
prune unrelated Docker resources. The index runner creates only a local OCI archive.

Run focused tests while developing; for implementation changes complete the clean
Release build with analyzers/full suite and affected native/browser/container checks
against final sources. Dispatch, security and persistence changes require the full suite.
Before committing C# changes:

```sh
dotnet format whitespace OnvifWebhookBridge.slnx
dotnet format whitespace OnvifWebhookBridge.slnx --verify-no-changes
```

For documentation-only work, verify all relative links/images/anchors, commands and
configuration claims against source, and run `git diff --check`. No documentation
linter is configured in CI. An optional local Markdown lint run must account for
tables and existing style; it is not a replacement for technical review.

## Diagnostic probe

A small exploratory .NET 10 console tool using SharpOnvifClient 0.11.2. It discovers the event service from a configured device-service endpoint, subscribes without topic filtering, and prints every returned notification. It does not change camera configuration or forward events anywhere.

### Run

Install the .NET 10 SDK. From the repository root:

```powershell
dotnet build src/OnvifProbe/OnvifProbe.csproj
$env:ONVIF_ENDPOINT = 'http://<camera-host>/onvif/device_service'
# Optional: set both credentials if the device requires authentication.
$env:ONVIF_USERNAME = '<username>'
$env:ONVIF_PASSWORD = '<password>'
dotnet run --project src/OnvifProbe -- --raw
```

Replace the placeholders locally. For an unauthenticated device, leave both credential variables unset. No endpoint or credential is hardcoded. Command-line options override environment variables:

```text
dotnet run --project src/OnvifProbe -- --endpoint <device-service-url> --raw
dotnet run --project src/OnvifProbe -- --help
```

`--username` and `--password` are also supported; prefer environment variables so credentials do not appear in process arguments or shell history. Bash uses `export ONVIF_ENDPOINT='http://<camera-host>/onvif/device_service'` and corresponding credential variables. A local `Properties/launchSettings.json` can supply arguments/environment for `dotnet run`; it is ignored by Git and must remain private. Use `--no-launch-profile` before `--` to bypass it. Press **Ctrl+C** to stop; cleanup attempts to unsubscribe for up to three seconds, then disposes the clients.

### Output and behavior

Each event has a local receipt timestamp with milliseconds and timezone, the full topic text/dialect, and the library-exposed message tree. Camera timestamps such as `UtcTime`, property operations, Source/Data/Key fields, duplicate names, boolean text, attributes, and structured/unknown elements are printed without an assumed schema. There is no topic filter, deduplication, or suppression of false states. `--raw` additionally prints the message XML exposed by the library, not a complete original SOAP envelope or byte-for-byte network capture.

Status messages report connecting, subscribing, renewing, reconnecting, and stopping. Endpoint URLs and credentials are omitted from status/error messages. Notification payloads themselves may contain private details: keep console output/captures private and sanitize before sharing. No files are written by the probe. Do not commit personal addresses, device identities, real configuration, or captures; `.local/`, `.env`, and `*.local.*` are ignored.

The probe requests a 120-second lease and pulls up to 100 notifications with at most a 20-second long poll. It uses camera-relative lease times for renewal through the generated SubscriptionManagerClient. Version 0.11.2 does not automatically include WS-Addressing headers on that client's renewal request. A small adapter adds Action, a unique MessageID, To, and anonymous ReplyTo through the supported header hook, while preserving the library's authentication. Without MessageID, subscription managers requiring it can return ActionFailed on every renewal. On faults, expired leases, or disconnects the probe disposes the client and attempts a fresh connection/subscription after three seconds. A small delay prevents devices returning immediately from causing a busy loop. Reconnection can lose events while disconnected; this diagnostic does not provide durable delivery.

The SimpleOnvifClient convenience calls do not expose CancellationToken in this version. Cancellable waits plus client disposal bound shutdown; renewal uses the generated API's cancellation token. Subscription reference parameters are not forwarded by the convenience methods: a warning identifies devices that return them. Such devices may require a lower-level client. Unknown XML already discarded by library deserialization cannot be recovered here. Subscription/producer reference addresses are intentionally omitted because they can contain private URLs/tokens. No built-in motion helpers are used.

## Compatibility and evidence

The historical probe run on one anonymous physical device received initialization
notifications, sustained at least 25 renewals over more than 40 minutes after the
WS-Addressing fix, and unsubscribed on Ctrl+C. That limited result establishes neither
complete activation/reset cycles nor interoperability with other devices. Historical
unspecified user-reported AOT results are not a substitute for target-specific checks.

The automated acceptance paths exercise actual onvifsim and local recorders under
JIT and NativeAOT. CI defines native amd64/arm64 execution, while local ARM64 runs may
be emulated. Inspect the exact revision's run instead of transferring older totals or
results to new code. Packaging tests measure process health, permissions, continued
per-camera delivery, persistence, shutdown and TLS; sparse resource observations are
not capacity benchmarks or guarantees. Registry read-back verifies published content,
not hardware behavior.

Before enabling a real transition mapping, privately observe at least two complete
activation/clear cycles, exact section/value, stable Source/Key identity and operations.
Use occurrence mode if reset is absent. Diagnose with the probe on a reachable network:
verify device/event/subscription routing, credentials and clocks; observe repeated
true/false and initialized/deleted messages; check several renewals and Ctrl+C cleanup.
Coordinate any real reboot/network test and verify one camera's outage does not affect
another. Do not alter firmware, detection or video settings incidentally.

Real-device activation/reset, real reboot/outage recovery, operator volume/network/CA/
proxy compatibility and real destination-side effects require separate validation.
Use a local recorder unless a real destination/action is explicitly authorized;
HTTP 2xx proves transport acceptance only. Preserve anonymous conclusions here and
keep device-identifying captures/configuration in ignored private storage.

## Screenshots

The [user-guide images](BROWSER_ADMINISTRATION.md#screenshots) use synthetic simulator
events and a local HTTP recorder. When replacing them, capture the actual packaged UI
with embedded assets; verify alignment with the current release and inspect text and
metadata for endpoints, credentials, tokens and device identities. Keep unsanitized
captures in ignored local storage. Do not treat screenshots as full integration or
hardware evidence.

## Docker Hub overview

[DOCKER_HUB.md](DOCKER_HUB.md) is the checked-in Docker Hub overview. After review and
merge, a maintainer can copy it to Docker Hub's overview editor; publishing an image
does not synchronize that text. Keep it aligned with the README using synthetic data.
