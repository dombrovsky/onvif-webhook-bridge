# Administrator setup and phase 6 API

The application requires one administrator before it starts HTTP listeners, the dispatcher or camera workers. There is no default account, browser setup wizard or public reset route. `/health` returns only `Healthy` when its persistent configuration read succeeds after startup, or an empty 503 on storage failure; it does not depend on camera readiness. All data and administration routes require a session except same-origin login. Phase 7 adds only a public empty login shell at `/` and its fixed `/app.js` and `/app.css` assets. See [browser usage](BROWSER_ADMINISTRATION.md).

## Initial setup

Set `ONVIF_DATA_DIRECTORY` to a dedicated private directory and supply `ONVIF_ADMIN_USERNAME` plus exactly one of:

- `ONVIF_ADMIN_PASSWORD`: a password of 12–4096 UTF-16 units.
- `ONVIF_ADMIN_PASSWORD_FILE`: a readable UTF-8 password file. A trailing CR/LF is removed. The read is bounded; passwords still have the same length limit. Restrict the file and its parent directory to the application/operator account.

Username must be nonblank and at most 256 units. Incomplete setup, conflicting password sources, unreadable/oversized password files or invalid new credentials stop startup with a fixed message and exit code 1. No account is created on invalid input. With no established administrator and no setup variables, startup also exits 1 before any workers or HTTP listeners start. Storage initialization may create an empty private database first.

Normal startup ignores all administrator setup variables once an administrator exists, including stale or unavailable password-file settings. Changing these variables never replaces the existing credential. Remove plaintext bootstrap variables when no longer needed. The database stores only the username and salted PBKDF2-HMAC-SHA256 password hash (600,000 iterations, random 16-byte salt, 32-byte result), retaining the phase 5 format. Each login verifies with a constant-time hash comparison; unknown usernames do the same adaptive work. There is no plaintext administrator password column or API to read the hash.

## Explicit offline password reset

1. Stop the bridge and ensure no other process uses the data directory. Keep a protected consistent backup according to [DATA_STORAGE.md](DATA_STORAGE.md).
2. Set the same `ONVIF_DATA_DIRECTORY`, the desired `ONVIF_ADMIN_USERNAME`, and one password source above.
3. Run the application executable with the sole operation flag `--reset-administrator`. For a development build: `dotnet run --no-launch-profile --project src/OnvifWebhookBridge -- --reset-administrator`.
4. Require exit code 0. The command initializes/validates storage, transactionally replaces the administrator hash and exits without starting HTTP listeners, dispatcher or workers. It can also create the first administrator. Failure returns 1 with a fixed diagnostic.
5. Clear the bootstrap secret from the environment as appropriate, then start normally.

Reset preserves cameras, rules, recoverable secrets and the configuration revision. It does not delete the database or bypass permissions/corruption checks. Sessions live only in process memory; stopping/restarting invalidates all old cookies. Run no reset against a live bridge: its in-memory sessions cannot be revoked from a second process. This is a local operator recovery policy, not a remote account-recovery feature.

## HTTP authentication and origin contract

`POST /api/login` accepts `{"username":"<administrator>","password":"<password>"}` with `Content-Type: application/json` and an `Origin` equal to the application's request scheme, host and port. Default ports are compared canonically. Missing, malformed, null or foreign origins fail with 403. Cross-site/same-site Fetch Metadata also fails; subdomains are not the same origin. No CORS policy allows cross-origin credential access. Login intentionally does not require a preexisting session/CSRF token.

Successful login returns `{"csrfToken":"<opaque-token>","expires":"<timestamp>"}` and a host-only `onvif-session` cookie with Path `/`, HttpOnly, SameSite=Strict and Secure when the application receives HTTPS. The session token and CSRF token are independent 256-bit cryptographically random values. Sessions expire absolutely after eight hours; activity does not extend expiry. At most 64 sessions exist; capacity rejects new logins instead of evicting an active session. Expired entries are reclaimed on access/login. Neither tokens nor sessions are persisted.

Use HTTPS for credentials/session transport beyond a controlled local development connection. The app does not trust forwarded headers or proxy-provided client addresses. A proxy's TLS termination alone does not make a backend HTTP request HTTPS to this app; see [deployment usage](DEPLOYMENT.md) for a validated HTTPS-backend proxy arrangement that preserves this trust boundary. Do not expose the development scaffold publicly or add arbitrary forwarded-header trust to obtain Secure cookies.

Login is bounded to five attempts per direct peer address and 20 globally per one-minute window, including successful attempts; at most 256 peer buckets and two concurrent password verifications exist. It returns 429 at capacity. Forwarded addresses cannot bypass the bound; peers behind a proxy share its quota. Invalid credentials receive a generic 401. There is no permanent account lockout. Request sizes and password lengths are checked before adaptive work.

Every other API request sends the session cookie. `GET /api/session` returns the CSRF token and absolute expiry for that session. Every mutation and manual test also requires the same `Origin` and `X-CSRF-Token` header from that session. This applies to PUT, DELETE and POST, including logout and connectivity/HTTP tests. An invalid or missing session gives 401; invalid CSRF/origin gives 403. `POST /api/logout` invalidates the session and deletes its cookie. Liveness is the sole anonymous API read.

All API responses use no-store caching, JSON's default HTML-sensitive escaping, nosniff and a restrictive CSP. Phase 7 serves embedded plain browser screens; names/XML are rendered as inert text. CSP permits same-origin scripts/styles/connections, forbids inline scripts and disallows base URLs, foreign form submissions and framing. No internal configuration snapshots, hashes, camera credentials, action URLs/headers/bodies or arbitrary exception messages are serialized. Inspector event content is intentionally preserved within its existing bounds; treat authenticated event inspection as sensitive.

## Routes and edits

| Route | Behavior |
| --- | --- |
| `GET /health` | Fixed process liveness text only; no camera/configuration/storage details. |
| `POST /api/login`, `GET /api/session`, `POST /api/logout` | Session operations above. |
| `GET /api/configuration` | Masked cameras/rules, configuration revision and administrator-presence flag. |
| `GET /api/cameras`, `GET /api/rules` | Masked lists. Read configuration for the revision before editing. |
| `PUT /api/cameras/{id}`, `PUT /api/rules/{id}` | Create or replace that public configuration, preserving omitted secrets. IDs are client-chosen stable route IDs. |
| `DELETE /api/cameras/{id}`, `DELETE /api/rules/{id}` | Delete with JSON `{"revision":<current revision>}`. A referenced camera returns 409 until its rules are deleted. |
| `POST /api/cameras/{id}/enabled`, `POST /api/rules/{id}/enabled` | JSON `{"revision":<current revision>,"enabled":true}` or false. |
| `GET /api/camera-status` | IDs, fixed state, timestamps, epochs/failure counts. Reachability is separate from subscribed readiness. |
| `GET /api/events` | The bounded recent history, including false/unmatched events, structured fields, raw previews and truncation flags. Topic is the exact matching string. |
| `GET /api/execution-status`, `GET /api/executions` | Latest admission/current status and latest completed execution per configured rule respectively; categories/counts/status/timing plus final-attempt `responseJson`, `responseBodyStatus` and safe `transportError`. JSON previews are bounded to 16 KiB and depth 32, transient, administrator-only and never logged or persisted. |
| `POST /api/cameras/{id}/test` | Test the configured camera with bounded GetServices, not subscription or activation. At most four tests concurrently, ten-second operation bound, cancellation/disposal and fixed failures. No supplied arbitrary endpoint is accepted here. |
| `POST /api/rules/{id}/test` | Explicitly admit the currently configured HTTP action; 202 means queued, not delivered. Same dispatcher, deadlines, revisions, retries and queue bounds; bypasses matching/cooldown. Requires an enabled camera, may test a disabled rule. Inspect execution results afterward. Rejection returns 409. |

Camera PUT JSON has required `revision`, `name`, `endpoint`, optional `enabled` (defaults false) and `username`/`password` secret edits. Rule PUT has required `revision`, `name`, `cameraId`, `topic`, optional `condition`, `enabled` (false), `mode` (0), `cooldownSeconds` (0), `method` (`GET`), `additionalAttempts` (0), `ignoreCertificateValidation` (false), and `url`/`headers`/`jsonBody` secret edits. PUT replaces public fields; include their desired values rather than treating PUT as a patch. Rule reads return the existing `match` record with `cooldown` as a JSON TimeSpan string; edit requests use numeric `cooldownSeconds`.

`condition` uses `section` (0 Source, 1 Data), `name`, `expectedValue`, `comparison` (0 Text, 1 Boolean). `mode` is 0 Occurrence or 1 Transition; Transition requires a condition. Undefined enum values fail validation. No schema/vendor field names are assumed. Retries and all matching/transition/cooldown behavior retain [the existing contracts](HTTP_VERIFICATION.md).

Secret edits are JSON objects with numeric `operation`: 0 Preserve (default), 1 Replace, 2 Clear. Replace requires a non-null `value`; Preserve/Clear forbid a replacement value. Omit the edit to preserve. Camera credentials/action URL/body use string values; headers use an array of `{"name":"<header-name>","value":"<header-value>"}`. Clear both camera credentials together for an unauthenticated camera. Clear optional headers/body independently; required action URL cannot be cleared. Reads give presence flags, never replacement values. First creation of a rule requires an explicit URL replacement.

All durable changes use the store transaction and expected revision before changing active snapshots. Successful edits return `{"revision":<new revision>}`. A stale edit returns 409 `RevisionConflict`: reread/review/resubmit, rather than automatically overwriting. Known errors have fixed `code` and `message` fields: 400 Validation/InvalidRequest, 403 Origin/Csrf, 409 RevisionConflict/CameraReferenced/NotAdmitted, 413 size, 415 JSON media type, 408 request deadline, 429 capacity, 503 StorageUnavailable. Missing test/enable targets return 404. Storage failures leave active configuration unchanged. Queued work is invalidated by existing camera/rule revisions; already sent HTTP cannot be recalled.

JSON uses source-generated metadata only, rejects unknown properties, missing required constructor fields and nulls for non-nullable fields, and retains the default depth limit of 64. JSON request bodies are capped at 256 KiB with a ten-second read deadline, even without Content-Length. Existing configuration limits remain: 64 cameras, 256 rules, bounded strings/headers/literal JSON, bounded history/identities/dispatch. Connectivity shares the reviewed pinned library client and existing DNS-checked HTTP transport policy: no metadata/link-local/unspecified/multicast addresses, redirects, proxy or cookie sharing; private destinations and ordinary certificate verification remain supported. Known self-target camera endpoints are rejected on API edits and known self-target actions remain ineligible. DNS aliases of the application are not universally identifiable.

Logging providers remain cleared; HttpClientFactory request loggers remain removed, and the ONVIF library logger is unset. Fixed API failures do not include provider/network exceptions or submitted values. Do not enable request-body/URL/header/SOAP logging or library diagnostics with real secrets. See [phase 6 verification](ADMIN_VERIFICATION.md) for the tested runtime boundaries and remaining hardware/platform gates.
