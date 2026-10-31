# Container deployment

This guide describes current `main` source; published prereleases may predate its implementation contracts. Install the published Docker Hub image using the [README Quick start](../README.md#quick-start). No checkout or build is required. The examples default to `latest`, which is not published yet; pulls will fail until the first stable release. The application is one .NET 10 NativeAOT process with embedded browser files and SQLite; it has no default password. Camera connections and webhook rules are configured **in the browser**, not through container environment variables.

## First start

The README uses `docker run`: `onvif-bridge-data` holds the database, `onvif-bridge-bootstrap` holds the private password file, and `onvif-bridge` is the container name. Docker pulls the image automatically when missing. The image supplies the internal HTTP port, data path and health check; the startup command adds restart and security restrictions. Check health with `docker inspect --format '{{.State.Health.Status}}' onvif-bridge`.

### Docker Compose alternative

For a declarative installation, use Bash and curl to resolve GitHub's latest stable
release, validate its tag and stage the matching Compose file. No stable release
exists yet; lookup/download failures stop this block, without falling back to the
development branch or a prerelease.

```bash
(
  release_url=$(curl --fail --silent --show-error --location \
    --proto '=https' --proto-redir '=https' --max-redirs 5 \
    --connect-timeout 10 --max-time 60 --output /dev/null \
    --write-out '%{url_effective}' \
    https://github.com/dombrovsky/onvif-webhook-bridge/releases/latest) || {
    printf '%s\n' 'Cannot resolve a stable release; check availability and connectivity.' >&2
    exit 1
  }
  stable_url='^https://github\.com/dombrovsky/onvif-webhook-bridge/releases/tag/v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$'
  if [[ ! $release_url =~ $stable_url ]]; then
    printf '%s\n' 'No valid stable release tag was resolved; installation cannot continue.' >&2
    exit 1
  fi
  release_tag=${release_url##*/}
  curl --fail --silent --show-error --location \
    --proto '=https' --proto-redir '=https' --max-redirs 5 \
    --connect-timeout 10 --max-time 60 --output docker-compose.yml.new \
    "https://raw.githubusercontent.com/dombrovsky/onvif-webhook-bridge/$release_tag/docker-compose.yml" || {
    printf '%s\n' 'Compose download failed; do not use the staged file.' >&2
    exit 1
  }
  printf 'Downloaded Compose for %s to docker-compose.yml.new\n' "$release_tag"
)
```

Continue only on success. Review `docker-compose.yml.new`, then use
`mv -i docker-compose.yml.new docker-compose.yml` in your installation directory.
Existing installations should follow [upgrades](#storage-backup-upgrades-and-recovery)
instead of overwriting their local configuration.

Initialize the password volume with the README command, then run `docker compose pull` and `docker compose up -d --no-build`. No checkout or build is needed.

The shipped file has no `build:` section. You can also paste this example into Container Station's Compose editor. No `.env` is needed for the defaults:

```yaml
services:
  bridge:
    image: ${ONVIF_IMAGE:-dombrovskyi/onvif-webhook-bridge:latest}
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
docker run --rm --network none --user 1654:1654 --read-only --cap-drop ALL --security-opt no-new-privileges:true --mount type=volume,src=onvif-bridge-data,dst=/data --mount type=volume,src=onvif-bridge-bootstrap,dst=/run/bootstrap,readonly --env ONVIF_ADMIN_USERNAME=admin --env ONVIF_ADMIN_PASSWORD_FILE=/run/bootstrap/administrator-password dombrovskyi/onvif-webhook-bridge:latest --reset-administrator
```

Require exit zero before `docker start onvif-bridge`. For an explicit offline reset with Compose, stop the bridge and take a protected consistent backup first. Run the interactive password command again using the **same bootstrap volume**, then `docker compose run --rm --no-deps bridge --reset-administrator`. Require exit zero before `docker compose up -d --no-build`. Cameras, rules and revisions are preserved; see [administrator policy](ADMINISTRATION.md).

### Migrate Docker run to Compose

Use the same image version/digest during migration; upgrade separately. These commands use the README's default names. If yours differ, substitute the exact existing container and volume names throughout.

1. Record the current image and both mounted volume names:

   ```sh
   docker inspect --format '{{.Config.Image}} {{.Image}}' onvif-bridge
   docker inspect --format '{{range .Mounts}}{{println .Destination .Name}}{{end}}' onvif-bridge
   ```

2. Stop the original container and verify it is stopped. Keep it for rollback. Never start Compose while this container, or any other process using its database, is running:

   ```sh
   docker stop onvif-bridge
   docker inspect --format '{{.State.Status}}' onvif-bridge
   ```

   Require `exited`. Before proceeding, take a consistent private backup of the complete data directory (including any SQLite sidecars) and bootstrap file from the stopped container:

   ```sh
   umask 077
   mkdir migration-backup
   docker cp onvif-bridge:/data/. ./migration-backup/data
   docker cp onvif-bridge:/run/bootstrap/. ./migration-backup/bootstrap
   ```

   Require each command to succeed. Keep this directory private; it contains recoverable secrets. Do not overwrite an existing backup.

3. Save the Compose example in a permanent directory, select the same image through `ONVIF_IMAGE` in `.env`, and **replace** its top-level `volumes` section with the following. Use the exact names recorded in step 1; do not initialize fresh volumes or rerun password setup:

   ```yaml
   volumes:
     bridge-data:
       external: true
       name: onvif-bridge-data
     administrator-bootstrap:
       external: true
       name: onvif-bridge-bootstrap
   ```

   Keep the service mounts `bridge-data:/data` and `administrator-bootstrap:/run/bootstrap:ro`. Match the original host binding/port with the Compose variables if customized. Run `docker compose config --quiet`, then `docker compose up -d --no-build` only after the original container is stopped.

4. Run `docker compose ps` and wait for `healthy`. Log in with the existing credentials and verify saved cameras/rules and their enabled states. Confirm `docker inspect` on the Compose container shows the original image ID and both exact volume names. Do not start the old container to compare configuration.

5. Keep the old container stopped until those checks pass. It is then safe to remove **only** that container with `docker rm onvif-bridge`; keep the volumes and private backup. Before removal, rollback is `docker compose stop` (verify its container is exited), then `docker start onvif-bridge`. After removal, stop Compose before recreating the old Docker run command with the same image and volumes. Never run both instances together. If restoring the backup becomes necessary, stop all database users first and follow [storage recovery](DATA_STORAGE.md); do not restore over a live database.

### Multiple independent installations

Docker-run container names, named volumes and published host ports are host-wide. Each independent installation must use a distinct **container name, persistent data volume name, administrator bootstrap volume name and host port**. Change the bootstrap volume name in both the password command and startup command, plus the data volume, `--name` and host side of `--publish`. Reusing a bootstrap volume can silently give a new installation another installation's administrator password.

With Compose, use a distinct project name/directory for separate container and data-volume names, set a distinct `ONVIF_BOOTSTRAP_VOLUME` and `ONVIF_PUBLISHED_PORT`, and initialize that exact bootstrap volume. Explicit external volume names remain global even with different Compose projects. Share existing volumes only when migrating the **same** installation with its original process stopped, as above.

### Configuration reference

Compose reads interpolation values from the shell or local `.env` (shell wins). These are **not automatically passed into the application**. `.env.example` lists optional overrides and contains no password. Use one installation directory/project consistently so Compose keeps the same data volume.

| Compose variable | Default | Status and valid values / interaction |
| --- | --- | --- |
| `ONVIF_IMAGE` | `dombrovskyi/onvif-webhook-bridge:latest` | Optional image reference; use a verified version or `repository@sha256:…` digest. GHCR alternative: `ghcr.io/dombrovsky/onvif-webhook-bridge:latest`. Version tags omit `v`. After the first stable release, `dombrovskyi/onvif-webhook-bridge:latest` or `ghcr.io/dombrovsky/onvif-webhook-bridge:latest` tracks stable releases. No `latest` exists yet; prereleases never create/update it. |
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

With Compose, `bridge-data` is a project-scoped named volume mounted at `/data`. The README Docker run command instead uses the explicit name `onvif-bridge-data`. Do not switch between these names expecting the same database. Follow the [migration procedure](#migrate-docker-run-to-compose) to reuse existing volumes safely. Keep the same project name/directory across upgrades; changing it can create an apparently empty installation. Never point two processes at one database. Named volumes avoid host UID and NAS ACL setup on a fresh install. Existing volumes do not inherit updated image ownership.

For a bind mount, replace `bridge-data:/data` with an absolute dedicated host directory. Arrange ownership **1654:1654** offline (or the mapped host account under rootless/user-namespace Docker); owner directory mode **0700**, DB and sidecar files **0600** are enforced. NAS ACLs must permit that identity to read/write and change modes. Fix ownership offline, never by running the service as root or making storage world-writable. Filesystem restrictions are not encryption at rest. See [storage](DATA_STORAGE.md).

If you already use a host password file, keep your existing secret configuration instead of migrating it: replace the `administrator-bootstrap` service mount with `secrets: [administrator_password]`, set `ONVIF_ADMIN_PASSWORD_FILE: /run/secrets/administrator_password`, remove the bootstrap top-level volume declaration, and add:

```yaml
secrets:
  administrator_password:
    file: ${ONVIF_ADMIN_PASSWORD_PATH:?Set a private readable password file path}
```

The old `ONVIF_ADMIN_PASSWORD_PATH` is only a Compose interpolation variable in that variant; the application never consumes it. Host files must be readable by container UID 1654; Compose local file secrets do not reliably remap uid/gid/mode. Restrict the parent directory as well. Keep this host-file secret variant when reviewing and merging new release Compose configuration; do not silently replace it with bootstrap-volume defaults.

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

Tunnel workstation port 18080 to NAS loopback **18081**, log in and verify Overview version. Add a **disabled synthetic** camera named `Synthetic persistence check`, endpoint `http://camera.example/onvif/device_service` (no connection/test), then recreate using `docker compose -p onvif-nas-test up -d --no-build --force-recreate`. Log in again and confirm it remains. Check health and volume mounts using `docker inspect`; compare the image ID with `docker image inspect dombrovskyi/onvif-webhook-bridge:latest`. Do not test real cameras or destinations without separate authorization. Startup/network creation and authenticated local HTTP are covered here; actual camera routing remains a separate test.

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

For upgrades, keep a consistent backup, stop, pull the current stable `latest` image, then recreate with the same volume. Transactional migrations run before workers/listeners. Unsupported future schemas fail closed; binary rollback may require restoring a compatible backup. Do not use `down --volumes` for upgrades. Container recreation clears sessions, history, transitions, cooldowns, queues and execution results; stored configuration remains. No replay occurs.

For a Docker run upgrade:

1. Run `docker stop onvif-bridge` and take the protected consistent data backup described above.
2. Run `docker pull dombrovskyi/onvif-webhook-bridge:latest` (or the GHCR alternative).
3. Run `docker rm onvif-bridge` to remove only the stopped container. The named volumes remain.
4. Repeat the README startup command with `latest` and **the same data/bootstrap volume names**.
5. Check Docker health, login, Overview version and saved configuration. Never remove the data volume during an upgrade.

For an upgrade, keep `ONVIF_IMAGE=dombrovskyi/onvif-webhook-bridge:latest` in `.env`
(or the GHCR alternative above):

1. Take a consistent protected data backup and save copies of your Compose file and local overrides in private storage. Keep the existing installation directory, project name, `.env` and administrator password file.
2. In that directory, run **only the parenthesized release-download Bash block** from the [Compose alternative](#docker-compose-alternative). It resolves the current latest stable tag and writes `docker-compose.yml.new`; it does not change the active Compose file, `.env`, password file or volume. Do not reinitialize the bootstrap volume or recreate `.env`. Stop if the block fails, even if an earlier staged file exists.
3. Compare `docker-compose.yml` with `docker-compose.yml.new` and review the release notes. Merge any local base-file edits into the candidate deliberately. Keep local overrides and check their compatibility with the new base; do not silently replace custom configuration. Review the new volume mappings and ensure they still select your existing named data volume.
4. After review, back up the active Compose file and promote the candidate. The commands below prompt before overwriting an existing backup or active file. Validate using the same override files/options you normally use, then pull and recreate. Automatic Compose overrides in the installation directory remain in place; if you normally specify `-f` options, retain them for every Compose command.

```sh
cp -ip docker-compose.yml docker-compose.yml.previous
mv -i docker-compose.yml.new docker-compose.yml
docker compose config --quiet
```

Continue only if validation succeeds and the resolved settings preserve your data
volume, ports, secret path and local overrides. Otherwise restore the previous file
and correct the configuration before pulling or recreating:

```sh
docker compose pull
docker compose stop bridge
docker compose up -d --no-build
docker compose ps
```

Do not edit or recreate `.env` or the password file during this procedure. If a new
stable release appears between resolution and pulling `latest`, repeat the download
and review so the Compose file and image follow the same release.

## Network diagnosis and mappings

Normal Compose bridge networking is sufficient for explicitly configured device endpoints. Verify DNS/routing/firewall from the container for the device service, the **discovered event address**, returned subscription address and HTTP destination. A reachable device endpoint alone is insufficient. Incorrect advertised addresses require fixing routing/device configuration deliberately, not inventing subscription URLs. No host networking or WS-Discovery is required. Private addresses are allowed; metadata/link-local/multicast/unspecified targets and identifiable self loops are rejected. No camera outage should produce a health/restart storm.

Use Events to discover exact topics and Source/Data values, including false and unmatched notifications. Inspect retained raw/structured content; select an observed field to create a reviewed disabled draft. Generic examples: an occurrence rule for synthetic topic `Synthetic/Detection` can GET `https://<destination-host>/<action-path>`; a POST rule can send literal `{"synthetic":true}` and an explicitly replaced custom header. These are placeholders, not live destination instructions. Never assume a boolean field name or translate namespace prefixes silently. Boolean matching recognizes XML true/1 and false/0; text matching is exact. Transition requires a known nonmatching-to-matching change; first/Initialized observations establish baseline. Privately verify at least two activation/reset cycles and stable Source/Key identity before enabling a real transition mapping; use occurrence mode when reset is absent.

Cooldown starts on bounded queue admission and delivery failure consumes it. Rejected/suppressed work is not replayed. Retries default to zero and can duplicate effects even for GET; at most two additional attempts and the existing deadlines/queue/revision checks apply. Manual tests perform actual external actions; use only an explicitly intended destination. A 2xx response proves transport acceptance only. See [browser usage](BROWSER_ADMINISTRATION.md) and [matching and delivery](BROWSER_ADMINISTRATION.md#matching-and-delivery).

## Platforms and maintainer references

Images target `linux/amd64` and `linux/arm64`; ARMv7 and Linux 32-bit x86 are
unsupported. Check the actual host's architecture, storage, routing and TLS trust.
Native CI, local emulation and physical-device interoperability are separate evidence.
See [development validation](DEVELOPMENT.md#validation) for repeatable checks and
[releasing](RELEASING.md) for versioning, CI publishing and release recovery.

## Recovering a failed release

Maintainers: see [release recovery](RELEASING.md#recovering-a-failed-release).
This anchor remains for messages emitted by the existing Release workflow.
