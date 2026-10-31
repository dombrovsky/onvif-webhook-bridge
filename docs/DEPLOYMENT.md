# Container deployment and release

Install the published Docker Hub image using the [README Quick start](../README.md#quick-start). No checkout or build is required. The application is one .NET 10 NativeAOT process with embedded browser files and SQLite; it has no default password. Camera connections and webhook rules are configured **in the browser**, not through container environment variables.

## First start

The README uses `docker run`: `onvif-bridge-data` holds the database, `onvif-bridge-bootstrap` holds the private password file, and `onvif-bridge` is the container name. Docker pulls the image automatically when missing. The image supplies the internal HTTP port, data path and health check; the startup command adds restart and security restrictions. Check health with `docker inspect --format '{{.State.Health.Status}}' onvif-bridge`.

### Docker Compose alternative

For a declarative installation, save the example below as `docker-compose.yml` in a permanent directory, or download it with:

```sh
curl -fsSLo docker-compose.yml https://raw.githubusercontent.com/dombrovsky/onvif-webhook-bridge/main/docker-compose.yml
```

Initialize the password volume with the README command, then run `docker compose pull` and `docker compose up -d --no-build`. No checkout or build is needed.

The shipped file has no `build:` section. You can also paste this example into Container Station's Compose editor. No `.env` is needed for the defaults:

```yaml
services:
  bridge:
    image: ${ONVIF_IMAGE:-dombrovskyi/onvif-webhook-bridge:0.1.0-rc.3}
    restart: unless-stopped
    user: "1654:1654"
    read_only: true
    cap_drop: [ALL]
    security_opt: [no-new-privileges:true]
    stop_grace_period: 15s
    environment:
      ASPNETCORE_HTTP_PORTS: ${ONVIF_CONTAINER_PORT:-8080}
      ONVIF_DATA_DIRECTORY: /data
      ONVIF_ADMIN_USERNAME: ${ONVIF_ADMIN_USERNAME:-admin}
      ONVIF_ADMIN_PASSWORD_FILE: /run/bootstrap/administrator-password
    ports:
      - "${ONVIF_BIND_ADDRESS:-127.0.0.1}:${ONVIF_PUBLISHED_PORT:-8080}:${ONVIF_CONTAINER_PORT:-8080}"
    volumes:
      - bridge-data:/data
      - administrator-bootstrap:/run/bootstrap:ro
    tmpfs:
      - /tmp:rw,noexec,nosuid,size=32m,mode=1777
    networks: [bridge]
volumes:
  bridge-data:
  administrator-bootstrap:
    external: true
    name: ${ONVIF_BOOTSTRAP_VOLUME:-onvif-bridge-bootstrap}
networks:
  bridge:
    driver: bridge
```

Before Compose `up`, create the external bootstrap volume and password using the README's interactive `docker run` command. Docker initializes a fresh volume mounted at `/data` from the image's private directory, already owned by **1654:1654**, mode **0700**. The shell runs as that account with `umask 077`, writing a mode **0600** password file. It does not initialize the application database. This command avoids installing helper tools, host `sudo`, or NAS-specific ownership commands. It requires an interactive terminal (`-it`); enter the password once and check the command exits successfully. Passwords are 12–4096 UTF-16 units; the application's validation is authoritative, including for non-ASCII characters. An invalid password stops startup safely. The secret is a file in a separate named volume, mounted read-only in the service. It remains recoverable plaintext: protect Docker administrator access and backups.

Start with `docker compose pull`, `docker compose up -d --no-build`, then `docker compose ps`. Wait for `healthy` (the first scheduled check may take about 30 seconds). A unique administrator password is mandatory on a new database; `admin` is just the configurable default username. Bootstrap settings are ignored once an administrator exists. Rewriting the password file does **not** change an existing login. Keep the external volume for recreation and recovery; it is never automatically removed by Compose.

For Docker run installations, stop `onvif-bridge`, back up its data, rerun the password command, then reset with:

```sh
docker run --rm --network none --user 1654:1654 --read-only --cap-drop ALL --security-opt no-new-privileges:true --mount type=volume,src=onvif-bridge-data,dst=/data --mount type=volume,src=onvif-bridge-bootstrap,dst=/run/bootstrap,readonly --env ONVIF_ADMIN_USERNAME=admin --env ONVIF_ADMIN_PASSWORD_FILE=/run/bootstrap/administrator-password dombrovskyi/onvif-webhook-bridge:0.1.0-rc.3 --reset-administrator
```

Require exit zero before `docker start onvif-bridge`. For an explicit offline reset with Compose, stop the bridge and take a protected consistent backup first. Run the interactive password command again using the **same bootstrap volume**, then `docker compose run --rm --no-deps bridge --reset-administrator`. Require exit zero before `docker compose up -d --no-build`. Cameras, rules and revisions are preserved; see [administrator policy](ADMINISTRATION.md).

### Possible future setup UI

A browser setup wizard would be an authentication change, outside this installation work. A safe design would require a short-lived, single-use local setup token, protected delivery of that token, same-origin/CSRF checks, bounded attempts, and transactional first-administrator creation before enabling workers or other APIs. Concurrent setup and restart/replay behavior would need dedicated tests. An unauthenticated publicly reachable first-user-wins setup page would weaken the current fail-closed behavior. The named password volume already removes host ownership commands without changing authentication.

### Configuration reference

Compose reads interpolation values from the shell or local `.env` (shell wins). These are **not automatically passed into the application**. `.env.example` lists optional overrides and contains no password. Use one installation directory/project consistently so Compose keeps the same data volume.

| Compose variable | Default | Status and valid values / interaction |
| --- | --- | --- |
| `ONVIF_IMAGE` | `dombrovskyi/onvif-webhook-bridge:0.1.0-rc.3` | Optional image reference; use a verified version or `repository@sha256:…` digest. GHCR alternative: `ghcr.io/dombrovsky/onvif-webhook-bridge:0.1.0-rc.3`. Version tags omit `v`. After the first stable release, `dombrovskyi/onvif-webhook-bridge:latest` or `ghcr.io/dombrovsky/onvif-webhook-bridge:latest` tracks stable releases. No `latest` exists yet; prereleases never create/update it. |
| `ONVIF_ADMIN_USERNAME` | `admin` | Bootstrap/reset only; nonblank, maximum 256 UTF-16 units. Compose passes this into the application. |
| `ONVIF_BOOTSTRAP_VOLUME` | `onvif-bridge-bootstrap` | Optional external Docker volume name; must already contain `administrator-password`, created as above. Use a distinct name for each independent installation. |
| `ONVIF_BIND_ADDRESS` | `127.0.0.1` | Optional host bind address; retain loopback for tunnels. LAN/all-interface binds require deliberate HTTPS/network configuration. |
| `ONVIF_PUBLISHED_PORT` | `8080` | Optional host TCP port, 1–65535; choose an unused port. `0` is useful only for tests requesting an ephemeral port. |
| `ONVIF_CONTAINER_PORT` | `8080` | Advanced internal TCP port, 1–65535; Compose sets both port mapping and `ASPNETCORE_HTTP_PORTS`. Use a single port to keep the probe working. |

The following are actual runtime inputs. With this Compose file, change fixed environment entries/mounts in the YAML if necessary; putting a value in `.env` only affects entries explicitly interpolated above.

| Application environment variable | Default | Status and interaction |
| --- | --- | --- |
| `ONVIF_DATA_DIRECTORY` | `/data` on Linux | Required writable persistent storage; shipped Compose fixes `/data`. Other operating systems default to `data` beside the executable. A different path needs a matching dedicated mount and ownership. |
| `ONVIF_ADMIN_USERNAME` | None in the application | Mandatory with a password source for first bootstrap or reset; Compose supplies `admin`. Ignored during normal startup after initialization. |
| `ONVIF_ADMIN_PASSWORD_FILE` | None | Preferred bootstrap/reset source. Compose fixes `/run/bootstrap/administrator-password`. Readable UTF-8 file, trailing CR/LF removed, maximum 16 KiB file with bounded character read; resulting password 12–4096 UTF-16 units. |
| `ONVIF_ADMIN_PASSWORD` | None | Alternative bootstrap/reset source; avoid plaintext environment secrets visible in container inspection. Mutually exclusive with the file source when bootstrap/reset runs. Not passed by shipped Compose. |
| `ASPNETCORE_HTTP_PORTS` | `8080` in image | Advanced .NET HTTP listener setting; also read directly by health probe. Probe requires one integer port, not .NET's semicolon-separated list. |
| `ASPNETCORE_HTTPS_PORTS` | Unset | Advanced standard .NET HTTPS port list; requires a configured certificate. Retain the separate HTTP health listener; named Kestrel endpoints override this setting. |
| `ASPNETCORE_URLS` / `--urls` | Unset | Standard .NET advanced listener overrides; can override HTTP ports, so retain an HTTP loopback health listener on `ASPNETCORE_HTTP_PORTS`. Not required for normal installation. |
| `Kestrel__Endpoints__Http__Url`, `Kestrel__Endpoints__Https__Url` | Unset | Advanced standard .NET endpoint configuration; see HTTPS example below. Named endpoints override URL/port settings. |
| `Kestrel__Certificates__Default__Path`, `Kestrel__Certificates__Default__Password` | Unset | Advanced HTTPS certificate path and optional PFX password; mount protected readable files. No project-specific certificate-password-file setting exists. |
| `DOTNET_EnableDiagnostics` | `0` in image | Advanced .NET diagnostic control; leave disabled for ordinary deployments. |

The application uses the standard .NET host configuration system for listener/runtime options; this is not a promise that arbitrary .NET settings are useful or supported by this deployment. Application-specific `ONVIF_*` values above are read directly from environment, not from JSON or CLI configuration. There are no container startup options for camera URLs, camera credentials, rule matching, retries, or per-webhook certificate policy. `ONVIF_ENDPOINT`, `ONVIF_USERNAME`, `ONVIF_PASSWORD` and `--raw` belong to the separate development probe, which is **not** the container entrypoint. Logging providers are deliberately cleared to prevent network diagnostics leaking secrets; standard log-level overrides do not enable request/body logging. HTTP action delivery and health probes disable proxies and redirects; `HTTP_PROXY`/`HTTPS_PROXY` are not a supported way to proxy webhook delivery.

| Executable operation | Behavior |
| --- | --- |
| No arguments | Initialize storage/bootstrap if needed, then start authenticated service; exit 1 on setup failure. |
| `--version` (alone) | Print compiled product version and exit without touching storage or starting listeners. |
| `--health-probe` (alone) | Check exact `Healthy` response at loopback `/health` on the configured HTTP port; exit 0/1, four-second deadline, no proxy/redirect. |
| `--reset-administrator` | Offline credential replacement and exit; stop all processes using this database first. |

Use `docker compose run --rm --no-deps bridge --version` for version inspection. Dockerfile `TARGETARCH`, `BUILDARCH`, `VERSION`, `REVISION` and BuildKit's `BUILDPLATFORM` are **build inputs**, not installation settings; `VERSION` must agree with source and does not override it.

### Storage and container restrictions

With Compose, `bridge-data` is a project-scoped named volume mounted at `/data`. The README Docker run command instead uses the explicit name `onvif-bridge-data`. Do not switch between these names expecting the same database. To move an existing Docker run installation to Compose, keep its volume by declaring `bridge-data: {external: true, name: onvif-bridge-data}` in the top-level `volumes` section. Keep the same project name/directory across upgrades; changing it can create an apparently empty installation. Never point two processes at one database. Named volumes avoid host UID and NAS ACL setup on a fresh install. Existing volumes do not inherit updated image ownership.

For a bind mount, replace `bridge-data:/data` with an absolute dedicated host directory. Arrange ownership **1654:1654** offline (or the mapped host account under rootless/user-namespace Docker); owner directory mode **0700**, DB and sidecar files **0600** are enforced. NAS ACLs must permit that identity to read/write and change modes. Fix ownership offline, never by running the service as root or making storage world-writable. Filesystem restrictions are not encryption at rest. See [storage](DATA_STORAGE.md).

If you already use a host password file, keep your existing secret configuration instead of migrating it: replace the `administrator-bootstrap` service mount with `secrets: [administrator_password]`, set `ONVIF_ADMIN_PASSWORD_FILE: /run/secrets/administrator_password`, remove the bootstrap top-level volume declaration, and add:

```yaml
secrets:
  administrator_password:
    file: ${ONVIF_ADMIN_PASSWORD_PATH:?Set a private readable password file path}
```

The old `ONVIF_ADMIN_PASSWORD_PATH` is only a Compose interpolation variable in that variant; the application never consumes it. Host files must be readable by container UID 1654; Compose local file secrets do not reliably remap uid/gid/mode. Restrict the parent directory as well. Keep existing deployment YAML when upgrading an older installation; downloading a replacement is unnecessary.

The service runs as **1654:1654**, with read-only root, all capabilities dropped and `no-new-privileges`. `/tmp` is a disposable 32 MiB `noexec,nosuid` tmpfs. No privileged initializer, Docker socket, host network, or extra service is needed. `restart: unless-stopped` restarts exited processes unless deliberately stopped; an unhealthy status alone does not restart a container. Image health checks run every 30 seconds, timeout 5 seconds, startup allowance 20 seconds, three failures before unhealthy; disconnected cameras do not fail process health.

Stop grace is 15 seconds, including workers' five-second cleanup target. Admission closes and queued/active work is cancelled. Host stop cancellation is honored, but final DI disposal waits for TaskFlow settlement so shared HTTP resources remain alive. A transport ignoring cancellation may outlive the grace period and be killed by Docker; completion and destination effects are then uncertain. Increasing grace does not make delivery durable.

## Remote access and NAS

For a remote Linux server or QNAP, run an SSH tunnel **on your workstation**, replacing the placeholders with your SSH account and host:

```sh
ssh -N -L 18080:127.0.0.1:8080 <ssh-user>@<docker-host>
```

Then open `http://localhost:18080`. For continuous shared access use the validated HTTPS-backend arrangement below. Do not assume Container Station's HTTP link is secure remote administration.

QNAP TS-451D2 requires the **linux/amd64** image. Check `docker version` and `docker compose version` over SSH; where only the older `docker-compose` command exists, check `docker-compose version` and validate the YAML with `docker-compose config` before importing. The documented commands target Compose v2 or later; Container Station versions differ, so update/use a compatible Compose installation if validation fails. If `curl` is unavailable, download the YAML on your workstation and upload/paste it through Container Station. Run the password initialization in an SSH terminal; the GUI does not provide the hidden interactive prompt. No Ubuntu `install` command, `sudo`, or host `chown` is required for the named-volume path.

### Isolated NAS validation

These commands are an operator-run test procedure, not authorization for automated NAS changes. First inspect existing containers, published ports and volumes read-only with `docker ps`, `docker volume ls`, and `docker network ls`. Confirm host port **18081** is unused; choose another if occupied. Use a new empty directory, fresh project/volume names and the downloaded YAML. Never reuse production YAML/data or change QVR Pro/camera settings.

Create this `.env` in that directory (choose unused names if they already exist):

```dotenv
ONVIF_BOOTSTRAP_VOLUME=onvif-nas-test-bootstrap
ONVIF_PUBLISHED_PORT=18081
```

Run the README password command with `src=onvif-nas-test-bootstrap`, then:

```sh
docker compose -p onvif-nas-test config --quiet
docker compose -p onvif-nas-test pull
docker compose -p onvif-nas-test up -d --no-build
docker compose -p onvif-nas-test ps
docker compose -p onvif-nas-test run --rm --no-deps bridge --version
docker inspect --format '{{.Image}} {{.Config.Image}}' "$(docker compose -p onvif-nas-test ps -q bridge)"
```

Tunnel workstation port 18080 to NAS loopback **18081**, log in and verify Overview version. Add a **disabled synthetic** camera named `Synthetic persistence check`, endpoint `http://camera.example/onvif/device_service` (no connection/test), then recreate using `docker compose -p onvif-nas-test up -d --no-build --force-recreate`. Log in again and confirm it remains. Check health and volume mounts using `docker inspect`; compare the image ID with `docker image inspect dombrovskyi/onvif-webhook-bridge:0.1.0-rc.3`. Do not test real cameras or destinations without separate authorization. Startup/network creation and authenticated local HTTP are covered here; actual camera routing remains a separate test.

Stop only this test with `docker compose -p onvif-nas-test down` when finished. This preserves its volumes. Remove test data only after verifying the exact test volume names; leave production containers/networks/volumes untouched. NAS interoperability must be reported separately from local Docker tests.

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

For upgrades, keep a consistent backup, stop, pull a chosen image digest, then recreate with the same volume. Transactional migrations run before workers/listeners. Unsupported future schemas fail closed; binary rollback may require restoring a compatible backup. Do not use `down --volumes` for upgrades. Container recreation clears sessions, history, transitions, cooldowns, queues and execution results; stored configuration remains. No replay occurs.

For a Docker run upgrade:

1. Run `docker stop onvif-bridge` and take the protected consistent data backup described above.
2. Run `docker pull <chosen-image-reference>` (replace the placeholder with the verified image tag/digest).
3. Run `docker rm onvif-bridge` to remove only the stopped container. The named volumes remain.
4. Repeat the README startup command with the chosen image and **the same data/bootstrap volume names**.
5. Check Docker health, login, Overview version and saved configuration. Never remove the data volume during an upgrade.

For a published-image Compose upgrade, retain your installation directory, project name, Compose file and both volumes. After stopping and backing up the complete data volume, set `ONVIF_IMAGE` in `.env` to the selected verified tag or digest, then run:

```sh
docker compose pull
docker compose up -d --no-build
docker compose ps
docker compose run --rm --no-deps bridge --version
```

Confirm health, login and saved configuration before considering the upgrade complete. A forced recreation of the same image is a safe persistence rehearsal, not proof of compatibility with a future schema. Never use `down --volumes`, delete the volume, or change project names as part of an upgrade.

## Network diagnosis and mappings

Normal Compose bridge networking is sufficient for explicitly configured device endpoints. Verify DNS/routing/firewall from the container for the device service, the **discovered event address**, returned subscription address and HTTP destination. A reachable device endpoint alone is insufficient. Incorrect advertised addresses require fixing routing/device configuration deliberately, not inventing subscription URLs. No host networking or WS-Discovery is required. Private addresses are allowed; metadata/link-local/multicast/unspecified targets and identifiable self loops are rejected. No camera outage should produce a health/restart storm.

Use Events to discover exact topics and Source/Data values, including false and unmatched notifications. Inspect retained raw/structured content; select an observed field to create a reviewed disabled draft. Generic examples: an occurrence rule for synthetic topic `Synthetic/Detection` can GET `https://<destination-host>/<action-path>`; a POST rule can send literal `{"synthetic":true}` and an explicitly replaced custom header. These are placeholders, not live destination instructions. Never assume a boolean field name or translate namespace prefixes silently. Boolean matching recognizes XML true/1 and false/0; text matching is exact. Transition requires a known nonmatching-to-matching change; first/Initialized observations establish baseline. Privately verify at least two activation/reset cycles and stable Source/Key identity before enabling a real transition mapping; use occurrence mode when reset is absent.

Cooldown starts on bounded queue admission and delivery failure consumes it. Rejected/suppressed work is not replayed. Retries default to zero and can duplicate effects even for GET; at most two additional attempts and the existing deadlines/queue/revision checks apply. Manual tests perform actual external actions; use only an explicitly intended destination. A 2xx response proves transport acceptance only. See [browser usage](BROWSER_ADMINISTRATION.md) and [matching and delivery](BROWSER_ADMINISTRATION.md#matching-and-delivery).

## Platforms, CI and releases

Images target linux/amd64 and linux/arm64. Native CI runners build and execute each target. Local amd64-to-arm64 cross-compilation uses emulation and must be labelled separately. ARMv7 is unsupported until toolchain/native assets and hardware smoke tests are established; Linux 32-bit x86 is unsupported. Check the actual deployment host's architecture, Linux container capability, volume ownership, routing and TLS trust. Simulator/recorder tests cannot establish camera transitions, physical reboot/outage recovery or real destination effects.

[`ci.yml`](../.github/workflows/ci.yml) uses native amd64/arm64 runners for full JIT tests, actual onvifsim, NativeAOT execution and production deployment tests. PRs need no registry credentials and have contents-read permissions only. The manually dispatched `Release` workflow in `images.yml` reuses that entire gate for the selected revision, then publishes its tested artifacts. Release runs share one concurrency group without cancellation; GitHub may replace an older pending run with a newer pending run, so avoid queuing several releases. Only the final publishing job receives contents-write and packages-write permission. See [GitHub's concurrency contract](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency). Publication requires explicit maintainer authorization and verification of registry access/manifests.

Dependencies retain their existing exact package pins; SDK/runtime manifest digests and Dockerfile frontend are pinned, as are Actions commit SHAs with reviewed tag comments. OS apt security packages are resolved at build time, so builds are not byte reproducible. To upgrade, inspect official manifests/source/release notes, update pins intentionally, rerun all targets including SQLite/TLS/auth/browser/simulator paths and inspect the resulting manifest before releasing. Native platform builds and manifest assembly follow [Docker's multi-platform guidance](https://docs.docker.com/build/ci/github-actions/multi-platform/); see [NativeAOT cross-compilation constraints](https://learn.microsoft.com/dotnet/core/deploying/native-aot/cross-compile). Do not broaden platform claims based on available base images alone.

## Repeating local verification

Requires PowerShell 7, .NET 10, a Linux Docker engine, the pinned simulator image (`tests/Build-OnvifSimulator.ps1`) and Node for the HTTPS client. The production browser walkthrough additionally needs an external Playwright module/browser; the application itself has no Node dependency. Build the native fixture first so its local HTTP/TLS recorder image is available:

The deployment soak requires recorder delivery from each camera on every cycle, alongside subscription and process health checks. Compose verification copies only YAML to an isolated directory, initializes a private password volume, checks health/UI/login, persists a disabled synthetic camera through recreation, checks password replacement through old/new login results and restores inherited environment values. It does not import the operator's `.env`.

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

The README Docker run smoke test extracts the startup command from the README, substitutes isolated names/port, and tests whitespace-password login, health, runtime restrictions, persistence after container removal/recreation, and the documented offline reset. It requires PowerShell 7 and Docker, without a build:

```powershell
./tests/Verify-DockerRun.ps1 -Pull -ExpectedVersion 0.1.0-rc.3 -ExpectedDigest sha256:4e22efec10be648348e399e00e6c83165da52075a9079170513c094f35e3be46
```

To repeat the source-free published-image check without building (PowerShell 7 and a Linux Docker engine):

```powershell
./tests/Verify-Compose.ps1 -Pull -ExpectedVersion 0.1.0-rc.3 -ExpectedDigest sha256:4e22efec10be648348e399e00e6c83165da52075a9079170513c094f35e3be46
```

The digest is the verified multi-platform index for `0.1.0-rc.3` in both registries; the runner pulls/executes AMD64. Pass `-PlaywrightModule <absolute-module-path>` to additionally test browser login and the persisted camera screen (Node plus Edge, or set `ONVIF_BROWSER_CHANNEL=chromium` with Chromium installed). CI runs this same Compose smoke against its candidate image on native AMD64/ARM64, with the browser check on AMD64; it does not pull a public release as a PR prerequisite.

### Docker Hub overview

[`DOCKER_HUB.md`](DOCKER_HUB.md) is the checked-in source for Docker Hub's repository overview. After review/merge, a Docker Hub maintainer should copy its Markdown into the repository overview editor. Image publication does not synchronize that text. Keep the Quick start/version aligned with the README when updating it.

## Public release readiness

Before publishing a release:

- Review the exact revision's [native AMD64/ARM64 CI results](../.github/workflows/ci.yml), tag protections and registry permissions. Keep device/host/destination limitations explicit; a past test result is not verification of a newer revision.
- Enable and verify private vulnerability reporting for a public repository; follow the [security policy](../SECURITY.md). Do not change visibility as an incidental test.
- Audit tracked files and relevant history for secrets and device/setup identities. Use a secret scanner plus manual review; automated scanning cannot certify inaccessible copies or detect every secret. Keep raw findings private. Do not rewrite history or publish artifacts without authorization.
- Keep the [UI screenshots](BROWSER_ADMINISTRATION.md#screenshots) aligned with the interface and use synthetic data without addresses, credentials or identifying payloads.
- Verify the intended image version/digest and both platform manifests. `ONVIF_IMAGE` selects the Compose image; published installations use `docker compose pull` and `docker compose up -d --no-build`. Retain password-file, volume ownership and bind defaults.

## Publishing a release

### Version source and workflow

`Directory.Build.props` is the only version definition: `VersionPrefix` is the three-number version and `VersionSuffix` is `rc.$(RcNumber)` or empty for stable. `RcNumber` uses GitHub Actions `GITHUB_RUN_NUMBER`, with local fallback `1`. Each new run increments the counter for that workflow; reruns retain the number, counters do not reset for a new prefix, and Verify and Release have separate counters. The reusable release verification uses its caller's counter. Docker receives the same counter as a build argument. `Version` and `InformationalVersion` combine them; `AssemblyVersion` and `FileVersion` remain numeric (`0.1.0.0` for all 0.1.0 candidates). The SDK's existing deterministic/CI and built-in SourceLink settings remain enabled; no additional SourceLink package is needed. The SDK may append the commit to informational metadata. The executable's exact `--version` command and authenticated session response remove that provenance suffix; Overview displays `v` plus that compiled version. `--version` needs neither administrator credentials nor storage and does not start the server.

Versions use SemVer without a leading `v`. Numeric prerelease identifiers cannot have leading zeroes. Build metadata (`+...`) is deliberately excluded from source release versions because Docker tags cannot represent it; provenance uses the separate revision label and informational metadata. Core components must fit numeric .NET assembly metadata (0..65534), and image tags are limited to 128 characters. Git tags and GitHub Release titles add `v`; image tags and product versions do not.

Version changes go through a small reviewed PR. The release workflow reads the prefix/suffix from `Directory.Build.props` at the selected commit; there is no version input in Actions. That derived version is passed unchanged to verification and publication and checked against the source again before registry writes. To reproduce a tagged candidate locally, set `GITHUB_RUN_NUMBER` to the candidate number and pass `--build-arg GITHUB_RUN_NUMBER=<N>` to Docker. Only prefix changes and switching to stable need a version PR; new candidates use the workflow counter. Do not change version literals in fixtures: they are synthetic test scenarios.

### One-time maintainer setup

1. Protect `main` with a branch rule/ruleset requiring reviewed PRs and the existing native verification checks. The workflow rejects an unprotected main branch. Allow native `ubuntu-24.04` and `ubuntu-24.04-arm` runners.
2. Configure tag rules for `v*` that prevent updates and deletion. Allow the workflow to **create** tags, without permitting force updates. Enable GitHub immutable releases where available. Avoid other publishers that can race this workflow or mutate its version tags.
3. Create a Docker Hub repository named `onvif-webhook-bridge` in the desired namespace. Choose visibility deliberately; the workflow never changes it. Set repository Actions variable **`DOCKERHUB_NAMESPACE`** to that user/organization namespace. The account used for authentication may differ from the organization namespace.
4. Set repository Actions secrets **`DOCKERHUB_USERNAME`** and **`DOCKERHUB_TOKEN`**. Use a Docker Hub access token with read/write access to this repository only where supported, without delete permission. The read-only preflight job needs these secrets too, so do not store them solely in the release environment. Enable immutable version tags in Docker Hub where available, excluding `latest`.
5. Allow `GITHUB_TOKEN` to read contents for verification, and write contents/packages in the publisher. No PAT is required. If a GHCR package already exists, link it to this repository and grant this repository's Actions access. Private packages/images require authorized pulls; do not make the repository or package public as a side effect of setup.
6. Create a GitHub environment named **`release`**, limited to `main`. Optionally require a reviewer approval (subject to the repository's GitHub plan). Approval occurs after the full verification gate and before publication. Do not add camera/destination credentials to release jobs. The publisher's `contents: write` permission is needed to create the exact-commit tag and draft/final Release; `packages: write` is used for GHCR.

Actions are official actions pinned to existing reviewed commit SHAs. The publisher installs the distribution's Skopeo package to copy an OCI layout without rebuilding. Authentication comes from the login actions' ephemeral Docker configuration; credentials are never command arguments or release assets. Login post-steps remove that configuration.

### GitHub UI procedure

After completing setup:

1. Merge the desired version-bump PR into `main`. Copy the **full 40-character SHA** of the main commit you intend to release from GitHub's commit page. The selected commit must be an ancestor of current protected main and contain this release implementation.
2. Open **Actions → Release → Run workflow**. Leave the branch selector on **main**. Enter the copied revision SHA, then run it. The workflow reads the version from that revision's `Directory.Build.props` and shows it in the run summary. The workflow refuses feature-branch dispatches, non-main revisions, source/version mismatches, existing tags/releases/drafts, and existing version tags in either registry. Authentication/network errors do not count as an available version.
3. Wait for the existing full native AMD64 and ARM64 suite. If configured, approve the `release` environment only after reviewing those checks.
4. A successful run publishes both multi-architecture image tags, verifies their exact digests/configurations, moves stable aliases when appropriate, and finalizes the GitHub Release. Follow its installation links and digest references. Do not manually create a release tag first.

| Intended release | VersionPrefix | VersionSuffix | Automatically resolved version | GitHub tag/type | Image tags in both registries |
| --- | --- | --- | --- | --- | --- |
| Release run 42 | `0.1.0` | `rc.$(RcNumber)` | `0.1.0-rc.42` | `v0.1.0-rc.42`, prerelease | `0.1.0-rc.42` only |
| Release run 43 | `0.1.0` | `rc.$(RcNumber)` | `0.1.0-rc.43` | `v0.1.0-rc.43`, prerelease | `0.1.0-rc.43` only |
| Stable | `0.1.0` | empty | `0.1.0` | `v0.1.0`, stable | `0.1.0`, then `latest` |

For `0.2.0` or `1.0.0`, bump the prefix and leave the suffix empty. Candidate runs can reuse the same merged SHA; stable releases require the merged empty-suffix change. The release workflow creates no automatic version-bump commit or PR. Use the normal main-branch CI before release; the release also repeats that same reusable gate with the chosen immutable SHA and source version.

### Images, installation and upgrades

Images are published to both Docker Hub and GHCR with the same tested content and multi-architecture index:

- Docker Hub (default): `dombrovskyi/onvif-webhook-bridge:<version>`
- GHCR alternative: `ghcr.io/dombrovsky/onvif-webhook-bridge:<version>`

Runnable architectures are exactly `linux/amd64` and `linux/arm64`. There are no public platform-specific moving aliases or minor/major aliases. Prereleases never touch `latest` or GitHub's latest stable designation. Stable releases update `latest` in both registries only after both immutable version manifests pass verification. Prefer explicit version tags or the index digest in the GitHub Release over `latest` for controlled upgrades.

Public registry pulls need no credentials. If an operator deliberately uses a private mirror, authenticate to that registry separately. Use the [configuration reference](#configuration-reference) for image selection and [upgrade procedure](#storage-backup-upgrades-and-recovery) to retain storage. Local image builds and OCI labels are covered in [development](DEVELOPMENT.md#local-container-builds).

### Artifact verification and deliberate limits

The reusable CI gate retains the existing simulator, tests, NativeAOT smoke, browser, Compose, TLS, deployment and soak checks on native runners. Its checkout is the selected SHA. Before export on each runner, the final production image must report the expected compiled version using `--version` without network, writable root, capabilities or administrator setup. Its architecture and OCI version/source/exact revision/MIT license labels must match. CI records the tested Docker config ID, archive SHA-256, binary version and commit alongside each saved image.

The publisher downloads only those artifacts from the **same workflow run**. It checks archive hashes and evidence, converts them once into a local OCI layout, checks the original tested config digests, validates labels again, and assembles exactly two runnable platform descriptors. Docker schema-2 platform configs are retained so conversion cannot strip the tested Docker health check. Skopeo copies that same layout to both registries using `--all --preserve-digests`; no application/image rebuild happens after tests. It reads back each registry's index and platform manifests, checks their SHA-256 digests and labels against the local evidence, and checks the exact release tag target before finalization. Generated notes include pinned image digests and setup links. The attached `release.json` records source, index/platform digests and tested-artifact hashes. Workflow evidence is retained for 90 days; tested image archives for 7 days.

A draft Release is created after verification and version reservation, before registry writes. It becomes public as a successful release only after both registries (and stable aliases, if any) pass verification. The workflow checks the final prerelease/draft state. There is no atomic transaction across GitHub and two registries: an intermediate failure is explicitly incomplete, leaves its draft unpublished, and may leave one registry/tag or one stable alias updated. See recovery below.

Standalone binary downloads are deferred: Docker remains the supported runtime contract, including OS dependencies, permissions and TLS assets. A bare NativeAOT executable does not carry that contract. Adding separate archive/runtime-support verification would add maintenance beyond this release path. No second binary is rebuilt for release assets. SBOMs, vulnerability scanners, Gitleaks in hosted release CI and attestations are also deferred; adding them later should include reviewed tool pins, base-image severity/suppression policy and explicit verification rather than unreliable default release blockers. Local secret review remains part of the implementation handoff.

### Recovering a failed release

Inspect the first failed step, the workflow summary, preserved `release-evidence`, the GitHub tag/draft, and **both registries**. A failed workflow is not a successful release even if one version tag is visible in a registry. Do not advertise a draft or manually finalize it after a failed verification.

- **Before tag reservation:** after fixing setup/transient errors, rerun using the same version/revision if no tag, draft, or registry version exists. Verification is rerun; expired CI artifacts cannot be silently substituted.
- **After tag reservation or any publication:** the version is consumed. Keep its tag/draft/evidence for diagnosis; never move/recreate the tag, overwrite assets/images, or use a rerun to repair it. Fix the problem through a PR, choose a new unused version (for example `0.1.0-rc.2` after a failed `rc.1`, or the next patch after a failed stable version), then start a new workflow run for that revision (a rerun keeps the consumed number). Any existing tag, draft, or version image causes the preflight to reject reuse, including a same-commit retry.
- **Stable alias failure:** versioned images may both be valid while only one `latest` moved. Treat the release as incomplete; use a known successful pinned digest while preparing the next stable version. Prereleases cannot repair/move stable aliases. Publication of that next verified stable version reconciles both aliases.
- **Failure after finalization:** inspect the actual Release state and digest evidence. A final state-check/API connection failure can leave an already published Release. Never rerun to overwrite it. Confirm its tag, both manifests and prerelease flag before treating it as successful; fix any issue with a new version.

Repository permissions/rules and registry immutability settings should prevent out-of-band mutations. Preflight collision checks plus serialized workflow writes cannot defend against an unrelated publisher with unrestricted concurrent write credentials. Do not run another publisher for these version tags.
