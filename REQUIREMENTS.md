# ONVIF Webhook Bridge requirements

Planning baseline: 2026-10-14. Updated status: phases 1–5 generic implementation complete through SQLite configuration persistence and startup restoration. Administration/authentication, UI and production deployment remain later work. Verification evidence and pending hardware/platform checks are recorded in IMPLEMENTATION_PLAN.md and the phase verification documents, including docs/PERSISTENCE_VERIFICATION.md.

## Purpose and scope

Build a small, self-hosted, vendor-independent bridge from ONVIF events to configurable HTTP actions. One process hosts ASP.NET Core Minimal APIs, a browser administration UI, camera subscriptions, matching, and dispatch. Use C#/.NET 10 with NativeAOT, SharpOnvifClient (baseline 0.11.2), and SQLite under `/data`.

Users configure their own ONVIF device endpoints and HTTP destinations. Event topics and fields must be discovered from notifications, with no vendor-specific integration in the core. Repository documentation and fixtures must remain generic and contain no personal deployment details.

The original planning-only session produced REQUIREMENTS.md, IMPLEMENTATION_PLAN.md and AGENTS.md. Subsequent authorization produced a console diagnostic prototype, solution/build scaffold, health-only application shell, Dockerfile and simulator-backed tests. Phases 2–4 add generic normalization/history/matching, bounded HTTP delivery, independent camera workers and reconciliation. Phase 5 persists configuration and recoverable secrets with transactional migrations, masked reads and startup restoration. The full administration/API/UI and production deployment remain future deliverables.

## Functional requirements

### R1 — Camera management and subscriptions

- Add, edit, enable, disable, and remove cameras. Store stable ID, name, device-service URL, username, and password; allow unauthenticated cameras.
- Test ONVIF connectivity with a bounded device/service request. Display the result separately from event-subscription readiness: reachable does not prove events work.
- Phase 4 resource bound: at most 64 cameras and 256 rules globally; reject oversized configuration snapshots before changing active state.
- Run one independent PullPoint subscription and sequential polling loop per enabled camera. Subscribe without topic or content filters; no WS-Discovery or callback listener is required.
- Discover event-service addresses from the configured device endpoint; respect subscription endpoint references, including reference parameters when supplied. Do not invent subscription URLs.
- Renew before expiration using returned timing information, and recreate subscriptions after expiration, reboot, transport failure, or invalid-subscription fault. Coordinate pull duration with remaining lease and HTTP timeout. Retry connection with capped exponential backoff and jitter, never a tight loop.
- Stop and dispose an old worker on disable/delete or connection-configuration changes; best-effort unsubscribe with a bounded deadline. Never have two active workers for one camera.
- Show disabled, connecting, subscribed, reconnecting, and error states, last successful contact, and last received event. No events during an idle long poll is not a failure.
- Camera failures must not stop other cameras, the inspector, UI, or dispatcher. Shutdown must cancel promptly, including outstanding network operations.

### R2 — Event discovery and preservation

- Retain the most recent 100 notifications globally in a bounded in-memory history, before matching or suppression. Include true, false, initialization, deletion, unmatched, and unknown vendor notifications.
- Preserve camera ID/name, local receipt time, camera timestamp when present (including original value if invalid), full topic and dialect/namespace context, Source, Data, Key fields, property operation, structured ElementItems, and additional XML/attributes when available.
- Represent fields as collections, not a lossy dictionary: preserve duplicate names, missing values, original text, and structured content. Do not hardcode motion/human topics or a `State` field.
- Keep original topic text plus a documented matching representation. Namespace prefixes are aliases: phase 1 must establish how the selected client exposes topics; do not blindly strip prefixes. Inspector and rule editor must show the exact string that matching uses.
- Expose recent/live events with lightweight polling and a details view. Creating a rule from an event prefills observed camera, topic, field section/name/value; it requires review and starts disabled.
- Bound retained raw XML and field sizes as well as count; mark truncation visibly. Malformed notifications produce a safe diagnostic and do not terminate polling. Full-property inspection must be possible during phase 1; any imposed limits and unavailable raw wire details must be recorded.

### R3 — Forwarding rules

Each independent rule has ID, friendly name, source camera ID, exact topic string, optional field selector (Source or Data), field name and expected value, GET/POST URL, optional custom headers, optional literal JSON POST body, enabled state, occurrence/transition mode, and cooldown seconds. No expression engine, templates, scripts, or wildcard matching.

Proposed baseline semantics (explicit assumptions pending confirmation):

- Topic, field names, and ordinary values use ordinal case-sensitive exact equality. A rule with no field condition matches the topic alone. Missing fields never satisfy a field condition; duplicate name entries match if any selected entry equals the expected value. Source and Data are selected explicitly to avoid ambiguity.
- Boolean values have a small explicit comparison mode: XML lexical `true`/`1` and `false`/`0` are equivalent; ordinary text mode remains exact. The UI exposes text/boolean choice; no arbitrary truthiness or case folding.
- Occurrence mode dispatches each matching notification subject to cooldown. Transition mode requires a field condition and dispatches only a known nonmatching-to-matching change. The first observation establishes a baseline without firing. Nonmatching/false observations update state even during cooldown.
- Transition identity is rule + camera + topic + complete stable Source/Key identity, excluding a selected Source condition field because its value can change. Phase 1 must validate this choice against observed messages. Without identity fields, use camera/topic. Bound the identity cache with documented eviction; eviction returns the identity to unknown baseline.
- Initialized messages establish state without transition dispatch; Deleted messages clear relevant state without transition dispatch. Occurrence mode treats both normally if matching. Unknown property operations remain visible and use ordinary matching.
- Reset transition state on application restart, new subscription epoch, rule edits, or enable/disable. Cooldown applies per rule across its identities, uses monotonic elapsed time, and starts only when a job is accepted into the dispatcher. Failed delivery consumes cooldown. A suppressed or rejected event is not replayed later.
- Multiple matching rules each produce an independent job; one failed rule must not block the others. State and cooldown checks are atomic so concurrency cannot produce duplicate admissions.
- Manual HTTP test is an authenticated explicit action, bypassing matching/cooldown but using the same dispatcher and limits. Show last execution time, result, attempts, and HTTP status code; response bodies are not logged or stored by default.

### R4 — HTTP delivery

- Use IHttpClientFactory for outbound actions. Configure headers per request; never share authorization between destinations. Validate HTTP/HTTPS URLs, method, headers, JSON body, and size limits. Reject embedded URL credentials, control characters, and attempts to override transport-controlled headers.
- Proposed defaults: 10-second timeout per attempt, 100 pending jobs globally, 4 active jobs globally, at most one active job per rule. Apply fair scheduling so a failing destination cannot monopolize dispatch. Queue overflow rejects the new job and records a safe status/counter; it does not stall subscription polling or remove inspector history.
- Phase 3 admission refinement (2026-10-15): cap pending jobs at 25 per rule within the global 100. Burst/overflow review showed that a single rule otherwise fills the entire queue before fair execution can help another rule. Reject new work at either bound; do not evict or replay accepted work. Round-robin execution remains FIFO within each rule.
- At most 3 total attempts, with short jittered delay and an overall 30-second job deadline. Retry transient transport failures, timeout, 408, 429, and 5xx; do not retry ordinary 4xx. Respect Retry-After only within that deadline. Dispose responses without reading unbounded bodies. Do not automatically follow redirects, which could disclose configured headers.
- Retries can duplicate effects, including GET endpoints that trigger actions. Proposed per-rule retry limit defaults to zero additional attempts; administrators can opt into up to two. No exactly-once promise, durable outbox, or replay after restart. Display this choice in rule configuration.
- Log delivery success/failure using IDs, safe error categories, duration, attempt count, and status only. Never log destination URLs, tokens, headers, request bodies, SOAP authentication, or unsanitized exception strings.

### R5 — UI, persistence, and security

- Three screens: Cameras (CRUD, status, connectivity test), Events (recent notifications/details/create-rule), Rules (CRUD, enable/disable, manual test, last execution result).
- Use static HTML/CSS and small plain JavaScript served by the same process; Minimal APIs with source-generated JSON metadata. No Node build, React, MVC/Razor dependency, frontend service, or SignalR requirement.
- SQLite persists cameras, rules, schema version, and administrator credential hash. Use parameterized SQL and small versioned transactional migrations, preferably Microsoft.Data.Sqlite without an ORM. Runtime history, transition state, queues, and execution status are in memory.
- Updates persist before workers/rules are reconciled. Deleting a referenced camera is rejected until its rules are removed; disabling preserves them and prevents event dispatch from that camera. Queued jobs for disabled/deleted/edited cameras or rules are invalidated before execution; already-sent HTTP cannot be recalled.
- One administrator, no default credentials. Proposed setup: initialize from administrator environment variables (support password file for container secrets); fail closed if no administrator exists. Store a salted adaptive password hash, not the initial plaintext password. Document explicit password reset and restart behaviour.
- Authenticate every UI/configuration/event/test route. Proposed AOT-friendly session: small opaque, cryptographically random, expiring server-side session cookie; bounded in-memory sessions; HttpOnly, SameSite=Strict, Secure under HTTPS. Require CSRF token and same-origin validation on mutation/test routes, limit login attempts, and HTML-escape all camera/XML content. Verify AOT compatibility before selecting any authentication framework.
- Camera passwords and secret action configuration must be recoverable for use; filesystem restrictions protect them, not a claim of encryption. Mask secrets in API/UI reads and support explicit replace/clear semantics. Restrict `/data` directory and DB/WAL/SHM files to the container account (target 0700/0600); document host volume ownership and backup sensitivity. At-rest encryption is deferred unless required.
- LAN-only intent; default Compose published address is loopback with explicit LAN bind override. Use HTTPS via a documented reverse proxy for access beyond localhost. An authenticated administrator can intentionally reach LAN destinations; permit private camera/destination addresses, but reject cloud metadata/link-local destinations and self-target loops where identifiable. No public exposure by default, disabled TLS validation, or blanket private-address ban.

### R6 — Deployment and release

- Multi-stage Docker build: .NET 10 SDK/native toolchain stage and compatible runtime-deps stage containing NativeAOT output, native SQLite dependencies, CA certificates, and required globalization/timezone assets. Fixed non-root UID; configurable internal/published port; mounted `/data`; graceful shutdown.
- Compose uses a normal bridge network, persistent volume, health check, and `restart: unless-stopped`. Explicit camera addresses should need no host networking. Verify both discovered event/subscription addresses and the destination are reachable from the container.
- Health reports application/persistence health without secrets; a disconnected camera does not make the entire container unhealthy or cause restart storms. Camera state is separate readiness information.
- GitHub Actions builds and verifies multi-platform images; publish only trusted branch/tag builds, not pull requests. Baseline targets are linux/amd64 and linux/arm64; evaluate linux/arm/v7 with a native toolchain and SQLite smoke test. Linux 32-bit x86 is excluded by .NET platform support, documented rather than falsely advertised. Confirm target CPU/OS/container capabilities before selecting an image.
- Future README covers setup, secrets, permissions, backup/restore, troubleshooting, platform support, and generic HTTP forwarding examples with placeholders. Focused automated tests cover parsing, exact/boolean matching, transitions, cooldown, dispatch, recovery, persistence, and access control.
- Keep bounded resource usage and record idle/loaded CPU, memory, image size, and configuration limits on a named test platform; no speculative performance guarantee.

## Exclusions

No video streaming/recording, FFmpeg, RTSP proxy, MQTT, firmware changes, mobile app, push infrastructure, workflow engine, plugin system, multi-user/RBAC, external database, broker, or vendor-specific core component. No automatic camera discovery in the initial product.

## Open questions and evidence status

| Question | Current assumption / required evidence | Gate |
| --- | --- | --- |
| Which event types and lifecycle does a test device expose? | Real initialization notifications and sustained renewal verified; activation/reset cycles and stable identity for a chosen real rule remain pending. Keep device identity and settings private. | Phase 2 real-rule validation |
| Can the coding environment reach a test device? | Successful real-device connect/subscribe/observation and clean shutdown verified using private configuration. Recheck reachability in later sessions. | Phase 1 complete; phase 4 recovery |
| Does released SharpOnvifClient preserve every extension and endpoint reference parameter under AOT? | Exposed message XML preserves structured/unknown content; full envelope is unavailable and convenience methods omit reference parameters. User verified NativeAOT/trimming; new processing paths and Linux targets need their own checks. | Phases 2, 4 and 8 |
| Transition initialization and identity semantics? | Proposed rules above avoid reconnect-triggered actions; revise from observed Source/Key fields before implementing matcher. | Phases 1–2 |
| Should retries be enabled by default? | Opt-in because destination actions may not be idempotent. Confirm destination behaviour independently. | Phase 3 |
| Administrator setup / transport? | Environment bootstrap, opaque session, reverse-proxy HTTPS proposed; no public first-run wizard. | Phase 6 |
| Secret encryption at rest required? | Restrictive files/volume and masked reads satisfy baseline; encryption would require a key-management decision. | Phase 5 |
| Which deployment architecture and container runtime? | amd64/arm64 baseline; arm/v7 conditional; Linux x86 unavailable. | Phase 8 |
| Destination TLS trust and action/result behaviour? | Operator-configured destination; no live delivery verification performed. Successful HTTP status alone does not prove the destination acted. | Phase 3 live verification |

Research sources and capability limitations are recorded in IMPLEMENTATION_PLAN.md. Proposed defaults are implementation choices, not observations of the camera.
