# Container deployment and release

Build locally with `docker compose build`. Nothing in these instructions requires image publication. The application remains one .NET 10 NativeAOT process, including the browser, authenticated APIs, independent camera workers and SQLite. It has no default administrator.

## First start

Copy `.env.example` to ignored `.env`. Replace the administrator name and password-file path locally. Create a private UTF-8 file containing a password of 12–4096 characters. Keep it outside source control; restrict its parent and read access. Compose file-backed secrets are mounted files, not an encrypted secret store. The container account (UID/GID 1654) must be able to read the file. On Linux, arrange ownership/access before startup; Compose does not reliably implement file-secret uid/gid/mode remapping. Do not put the password in `.env` or shell arguments.

```sh
docker compose config --quiet
docker compose build
docker compose up -d
docker compose ps
```

The default published interface is loopback. Open the configured local port and log in. Set `ONVIF_PUBLISHED_PORT` to change the host port and `ONVIF_CONTAINER_PORT` to change the single internal HTTP port. `ASPNETCORE_HTTP_PORTS` and the built-in `--health-probe` use that same single port; overriding Kestrel HTTP URLs requires updating probe configuration accordingly. `ONVIF_BIND_ADDRESS` changes the published interface deliberately. Use HTTPS for access beyond a controlled local connection.

The named volume is initialized from the image's private `/data`, owned by UID/GID 1654. Existing/bind-mounted directories and DB/sidecars must already belong to that account with owner access; directory mode 0700 and file mode 0600 are enforced. Fix ownership offline, never by running the service as root or making storage world-writable. A new named volume works without a privileged init service. `ONVIF_DATA_DIRECTORY` can select another dedicated mount in a Compose override, with the same ownership contract. Root filesystem is read-only, `/tmp` is a bounded disposable tmpfs, all capabilities are dropped and privilege escalation is disabled. Stop allowance is 15 seconds, including the workers' documented five-second cleanup target.

Bootstrap variables are ignored after the administrator exists. Keep the private file available for the shipped Compose definition, which requires its path at configuration time; startup ignores it after bootstrap. A separately reviewed override can remove bootstrap fields and their required substitutions. Retain a safe operator reset procedure. For offline reset, stop the bridge first, keep a consistent protected backup, then run `docker compose run --rm --no-deps bridge --reset-administrator` with the desired name/password file. Require exit zero before starting normally. This preserves cameras, rules and revisions. See [administrator policy](ADMINISTRATION.md).

## HTTPS and trusted certificates

The app intentionally does not trust forwarded headers. Preserve that boundary: use a reverse proxy which connects to a **validated HTTPS backend**, preserving the original Host header including a nondefault public port. Both browser and backend requests then have scheme HTTPS, so same-origin checks and Secure cookies work without trusting arbitrary forwarding headers. Do not terminate TLS to an HTTP backend and expect forwarded headers to fix cookies/origins.

Use a local Compose override to configure Kestrel's standard HTTPS endpoint, alongside the internal HTTP health endpoint:

```yaml
services:
  bridge:
    environment:
      Kestrel__Endpoints__Http__Url: http://+:8080
      Kestrel__Endpoints__Https__Url: https://+:8443
      Kestrel__Certificates__Default__Path: /run/secrets/backend_certificate
    secrets:
      - backend_certificate
    # Attach the operator-managed proxy to this private bridge network.
    # Do not publish the backend HTTP port on a LAN interface.
secrets:
  backend_certificate:
    file: ./.local/backend.pfx
```

Use a restricted PFX containing the private key, readable by 1654. If password-protected, supply Kestrel's certificate password through protected operator configuration. The proxy must trust the issuing CA, validate the backend certificate name and preserve Host. For example, in an operator-managed nginx HTTPS server block:

```nginx
location / {
    proxy_pass https://bridge:8443;
    proxy_set_header Host $http_host;
    proxy_ssl_server_name on;
    proxy_ssl_name bridge;
    proxy_ssl_verify on;
    proxy_ssl_trusted_certificate /run/secrets/backend_ca.pem;
}
```

The synthetic service name `bridge` must be in that backend certificate's SAN. Supply the proxy's public certificate and trust separately. Restrict backend network access to trusted peers. Direct peer throttling is shared by clients behind the proxy; client address forwarding is not enabled. Verify login, Secure cookie, same-origin mutations, logout and restart through the actual operator proxy before exposure. Proxy/host interoperability remains a deployment gate, not established by local simulator tests.

Outbound HTTPS uses normal system CA validation for both ONVIF and actions. The pinned Noble runtime includes CA certificates, ICU and timezone assets. For private issuing CAs, build a local derived image from a dedicated private build context as root, copy only the CA certificate to `/usr/local/share/ca-certificates/`, run `update-ca-certificates`, then restore `USER 1654:1654`; never copy CA private keys or disable validation. Rebuild after CA/base updates. Certificates must have correct names, validity and chain; also check system time. No global certificate bypass is supported.

## Storage, backup, upgrades and recovery

`/health` is anonymous fixed health text (or empty 503) and tests readable persistent configuration after startup administrator enforcement. The probe exits zero only for exact success, uses loopback without proxy/redirects, and has a four-second deadline. A disconnected camera does not fail process health. Camera readiness/contact/event state is shown separately. An unhealthy Docker status alone does not cause Docker restart; restart policy addresses process exits. Startup corruption/schema/permissions failures exit safely without deleting data.

Follow [the full storage/backup contract](DATA_STORAGE.md). Secrets in SQLite and backups are recoverable plaintext, protected by permissions rather than encryption. Stop the service before a filesystem backup; preserve the complete directory and sidecars. Alternatively use SQLite's online backup API to create a consistent snapshot. Never copy only a live main DB and discard WAL. Restore into a separate private correctly owned directory with the service stopped; retain the original. Start a single process on that directory and check schema, configuration, masked secrets and subscription state. Restore re-enables saved actions, so review the chosen backup before startup.

For upgrades, keep a consistent backup, stop, build/pull a chosen immutable image digest, then recreate with the same volume. Transactional migrations run before workers/listeners. Unsupported future schemas fail closed; binary rollback may require restoring a compatible backup. Do not use `down --volumes` for upgrades. Container recreation clears sessions, history, transitions, cooldowns, queues and execution results; stored configuration remains. No replay occurs.

## Network diagnosis and mappings

Normal Compose bridge networking is sufficient for explicitly configured device endpoints. Verify DNS/routing/firewall from the container for the device service, the **discovered event address**, returned subscription address and HTTP destination. A reachable device endpoint alone is insufficient. Incorrect advertised addresses require fixing routing/device configuration deliberately, not inventing subscription URLs. No host networking or WS-Discovery is required. Private addresses are allowed; metadata/link-local/multicast/unspecified targets and identifiable self loops are rejected. No camera outage should produce a health/restart storm.

Use Events to discover exact topics and Source/Data values, including false and unmatched notifications. Inspect retained raw/structured content; select an observed field to create a reviewed disabled draft. Generic examples: an occurrence rule for synthetic topic `Synthetic/Detection` can GET `https://<destination-host>/<action-path>`; a POST rule can send literal `{"synthetic":true}` and an explicitly replaced custom header. These are placeholders, not live destination instructions. Never assume a boolean field name or translate namespace prefixes silently. Boolean matching recognizes XML true/1 and false/0; text matching is exact. Transition requires a known nonmatching-to-matching change; first/Initialized observations establish baseline. Privately verify at least two activation/reset cycles and stable Source/Key identity before enabling a real transition mapping; use occurrence mode when reset is absent.

Cooldown starts on bounded queue admission and delivery failure consumes it. Rejected/suppressed work is not replayed. Retries default to zero and can duplicate effects even for GET; at most two additional attempts and the existing deadlines/queue/revision checks apply. Manual tests perform actual external actions; use only an explicitly intended destination. A 2xx response proves transport acceptance only. See [browser usage](BROWSER_ADMINISTRATION.md) and [delivery contracts](HTTP_VERIFICATION.md).

## Platforms, CI and releases

Baseline images target linux/amd64 and linux/arm64. Local arm64 execution is emulation, separately labelled in [deployment evidence](DEPLOYMENT_VERIFICATION.md); actual deployment hardware remains pending. Native runners build their own target; the local cross toolchain supports amd64-to-arm64 only. Cold emulated HTTPS needed bounded anonymous transport readiness before login tests; this does not establish native hardware handshake performance. ARMv7 is excluded until the native compiler/toolchain, SQLite runtime and hardware smoke gates pass. Linux 32-bit x86 is unsupported and is never in manifests. Check CPU architecture, Linux container capability and target OS before selecting images.

Hosted native amd64/ARM64 verification passed for the merged browser revision `fa0fad6` on 2026-10-21; see [current release readiness](#public-release-readiness). Historical results and [remaining hardware/host/destination gates](DEPLOYMENT_VERIFICATION.md#remaining-gates-and-concrete-handoff) retain their original scope. Hosted CI does not establish operator-host interoperability or authorize publication.

`ci.yml` uses native amd64/arm64 runners for full JIT tests, actual onvifsim, NativeAOT execution and production deployment tests. PRs need no registry credentials and have contents-read permissions only. `images.yml` runs that gate for version tags, then loads the verified platform artifacts, pushes per-platform tags and assembles the version manifest. Runs for the same tag are serialized without cancelling an active run, so concurrent publishers cannot mix the shared platform tags during manifest assembly. Only those publishing jobs receive packages-write permission. Restrict release tag creation, updates and deletion using repository rules; treat released tags as immutable and do not publish from untrusted PR code. No mutable latest tag is created. Registry publication is intentionally not exercised during local development. See [GitHub's concurrency contract](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency); hosted serialization/publication remains a separate verification gate.

Dependencies retain their existing exact package pins; SDK/runtime manifest digests and Dockerfile frontend are pinned, as are Actions commit SHAs with reviewed tag comments. OS apt security packages are resolved at build time, so builds are not byte reproducible. To upgrade, inspect official manifests/source/release notes, update pins intentionally, rerun all targets including SQLite/TLS/auth/browser/simulator paths and inspect the resulting manifest before releasing. Native platform builds and manifest assembly follow [Docker's multi-platform guidance](https://docs.docker.com/build/ci/github-actions/multi-platform/); see [NativeAOT cross-compilation constraints](https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile). Do not broaden platform claims based on available base images alone.

## Repeating local verification

Requires PowerShell 7, .NET 10, a Linux Docker engine, the pinned simulator image (`tests/Build-OnvifSimulator.ps1`) and Node for the HTTPS client. The production browser walkthrough additionally needs an external Playwright module/browser; the application itself has no Node dependency. Build the native fixture first so its local HTTP/TLS recorder image is available:

The deployment soak requires recorder delivery from each camera on every cycle, alongside subscription and process health checks. Compose verification uses isolated synthetic setup, checks password replacement through old/new login results and restores inherited environment values. It does not import the operator's `.env`.

```powershell
./tests/Verify-Phase7Native.ps1 -Platform linux/amd64
docker build --platform linux/amd64 -t onvif-webhook-bridge:local .
./tests/Verify-Deployment.ps1 -Image onvif-webhook-bridge:local -Platform linux/amd64 -SoakSeconds 600
./tests/Verify-Compose.ps1 -Image onvif-webhook-bridge:local
./tests/Verify-InboundTls.ps1 -Image onvif-webhook-bridge:local -Platform linux/amd64
./tests/Verify-Workflows.ps1
./tests/Verify-ImageIndex.ps1 -Build
```

For local ARM64 emulation on an amd64 builder, run the native fixture with `-Platform linux/arm64 -CrossCompile`, build the production image with `--platform linux/arm64`, and use that platform/image for deployment and inbound TLS verification. Keep separate image tags for each local target. Report emulation explicitly; native ARM64 hardware, real device mappings/outages and deployment-host interoperability remain independent gates. The index runner builds only a local ignored OCI archive and never publishes it. Docker runners remove their own uniquely named fixtures/volumes, preserving unrelated resources; ignored source staging and local images remain cached.

## Public release readiness

Documentation review on 2026-10-21 confirmed that the merged browser implementation at `fa0fad63c635c8257551f023e00dc2fdb47051e5` passed the hosted [Verify run 39364042748](https://github.com/dombrovsky/onvif-webhook-bridge/actions/runs/39364042748). This supersedes the older hosted-verification gap above for that revision; historical verification records retain their original dates and scope. It does not establish real-device or operator-host interoperability.

No GitHub releases were listed at review time. Registry inventory could not be verified with the available package permissions, so the README uses a local build and makes no public image availability claim. The existing trusted `v*` tag workflow targets `ghcr.io/dombrovsky/onvif-webhook-bridge:<version-tag>` with amd64/arm64 platform tags and a combined version manifest; it does not publish `latest`. Docker Hub is a possible future target and has no publishing configuration here.

Before offering a public release:

- Review the exact release revision's native amd64/arm64 CI results, tag protections and existing [hardware/host/destination gates](DEPLOYMENT_VERIFICATION.md#remaining-gates-and-concrete-handoff). Keep unverified device behavior explicit; inspect current workflow logs rather than relying on historical test totals.
- Establish and test a private vulnerability-reporting channel; see [security](../SECURITY.md). Its availability was not confirmed during this review.
- Review tracked files and Git history for secrets and private device data before changing visibility. The documentation diff audit is not a full history audit.
- Capture the three sanitized [UI screenshots](BROWSER_ADMINISTRATION.md#screenshots).
- When image publication is separately authorized, verify GHCR package access, trusted publication and the resulting platform manifest. Only then add a tested immutable image reference to the quick start. `ONVIF_IMAGE` selects the image in Compose; use `docker compose pull` and `docker compose up -d --no-build` for a verified published image. Keep the password-file, volume and bind defaults unchanged.

The [Sponsors profile](https://github.com/sponsors/dombrovsky) was verified active on 2026-10-21; `.github/FUNDING.yml` links it. Sponsorship is optional. This documentation change does not publish images, create a release, enable reporting, or change repository visibility.
