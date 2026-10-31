# ONVIF Webhook Bridge

[![Docker Hub](https://img.shields.io/badge/Docker_Hub-2496ED?logo=docker&logoColor=white)](https://hub.docker.com/r/dombrovskyi/onvif-webhook-bridge)
[![GHCR](https://img.shields.io/badge/GHCR-2496ED?logo=github&logoColor=white)](https://github.com/dombrovsky/onvif-webhook-bridge/pkgs/container/onvif-webhook-bridge)

Turn IP camera detection events into HTTP webhooks.

A lightweight, self-hosted Docker application that monitors ONVIF camera events and forwards matching notifications to configurable HTTP endpoints.

Configure cameras and event rules from a browser. No cloud account required.

[![Dashboard with a subscribed synthetic camera and an accepted HTTP delivery](docs/images/overview.png)](docs/images/overview.png)

## Features

- **Multiple cameras:** independent ONVIF PullPoint connections, lease renewal and automatic reconnection.
- **Event Explorer:** inspect received topics, Source/Data/Key fields and retained XML, including false and unmatched notifications. Draft rules from observed events.
- **Configurable forwarding:** exact topic and optional text/boolean field matching, occurrence or transition modes, cooldowns, GET or POST, custom headers and literal JSON bodies.
- **Browser administration:** manage cameras and rules, check connection status, test saved HTTP actions, and inspect recent delivery results.
- **Persistent configuration:** cameras, rules and administrator setup survive container recreation in a SQLite data volume.
- **Self-contained deployment:** one .NET NativeAOT process serves the UI, polls cameras and delivers actions; no external database or frontend service is needed.
- **Protected administration:** one administrator, no default credentials, bounded sessions, CSRF protection and masked secret reads. Recoverable camera and webhook secrets require private storage; see [security](SECURITY.md).

## How it works

```mermaid
flowchart LR
    Cameras[ONVIF cameras] -->|Events| Bridge[ONVIF Webhook Bridge]
    Bridge -->|Matching HTTP requests| Endpoints[HTTP endpoints]
```

Add camera device-service URLs explicitly. The bridge discovers each camera's event service, subscribes, and keeps a bounded recent event history. Rules select which observations produce HTTP requests; a failed camera or action does not stop the other camera connections.

## Quick start

You need Docker with Linux containers on AMD64 or ARM64. These commands use published **0.1.0-rc.3** (a prerelease); no Compose, checkout, or build is needed. Run them in a terminal on the Docker host.

Create the private password file in a Docker volume. Enter a unique password of 12–4096 characters at the hidden prompt; remember it for login:

```sh
docker run --rm -it --mount type=volume,src=onvif-bridge-bootstrap,dst=/data --entrypoint /bin/bash dombrovskyi/onvif-webhook-bridge:0.1.0-rc.3 -c 'umask 077; IFS= read -r -s -p "Administrator password: " password && printf "\n" && test "${#password}" -ge 12 && test "${#password}" -le 4096 && printf "%s" "$password" > /data/administrator-password'
```

Start the application with persistent storage:

```sh
docker run -d --name onvif-bridge \
  --restart unless-stopped --stop-timeout 15 \
  --user 1654:1654 --read-only --cap-drop ALL \
  --security-opt no-new-privileges:true \
  --publish 127.0.0.1:8080:8080 \
  --mount type=volume,src=onvif-bridge-data,dst=/data \
  --mount type=volume,src=onvif-bridge-bootstrap,dst=/run/bootstrap,readonly \
  --tmpfs /tmp:rw,noexec,nosuid,size=32m,mode=1777 \
  --env ONVIF_ADMIN_USERNAME=admin \
  --env ONVIF_ADMIN_PASSWORD_FILE=/run/bootstrap/administrator-password \
  dombrovskyi/onvif-webhook-bridge:0.1.0-rc.3
docker ps --filter name=onvif-bridge
```

Open [http://localhost:8080](http://localhost:8080) on the Docker host and log in as `admin` with your password. For a remote server or NAS, use an [SSH tunnel or HTTPS setup](docs/DEPLOYMENT.md#remote-access-and-nas); the default port is private to the host. Add a camera in **Cameras**, then use **Event Explorer** to draft and review a forwarding rule.

For multiple independent installations, use distinct container names, data volumes, bootstrap volumes and host ports; update the bootstrap name in both commands. See [deployment guidance](docs/DEPLOYMENT.md#multiple-independent-installations).

For GHCR, replace the image in both commands with `ghcr.io/dombrovsky/onvif-webhook-bridge:0.1.0-rc.3`. For Compose, see the [Compose example and configuration reference](docs/DEPLOYMENT.md#docker-compose-alternative), plus [upgrades and recovery](docs/DEPLOYMENT.md#storage-backup-upgrades-and-recovery). Local builds are covered in [development](docs/DEVELOPMENT.md#local-container-builds).

After the first stable release, you can use `dombrovskyi/onvif-webhook-bridge:latest` (or the GHCR equivalent). **`latest` is not available yet**; prereleases never create or update it. The release workflow updates it in both registries only for non-prerelease releases.

### Forward your first event

1. In **Cameras**, choose **Add camera**, enter its ONVIF device-service URL and credentials, and enable the connection. **Test connectivity** checks reachability; wait for subscription status separately.
2. Open **Event Explorer**, generate a detection at the camera, and inspect the actual topic and fields. Event support depends on the camera and its existing configuration.
3. Draft a rule from the observed topic or a Source/Data value. Review the match and set your HTTP destination, method and optional headers/JSON. New rules save **disabled**. Mark URLs containing tokens as sensitive.
4. Use the rule's manual HTTP test and inspect its execution result. **This sends a real request**, even for a disabled rule; the camera must be enabled. A 2xx response confirms HTTP acceptance, so verify the intended effect at the destination separately.
5. Enable the reviewed rule, generate another event, and check delivery. Use occurrence mode initially; transition mode needs a verified activation/reset cycle and stable event identity.

See [browser administration](docs/BROWSER_ADMINISTRATION.md) for matching, secrets, retries and execution details. Retries are off by default and can duplicate external actions.

For example, the synthetic event shown below maps topic `Synthetic/Detection/Person` and Data field `State` equal to `true` to a POST to `https://receiver.example/webhook` with literal JSON body `{"event":"person-detected"}`. Use the topic and fields you actually observe on your camera.

## Example uses

- Forward detection events to QNAP QVR Pro through its HTTP event mechanism, using a destination URL configured for that system.
- Trigger home automations that expose HTTP webhook endpoints.
- Send camera events to a custom application's HTTP handler using a configured GET or literal JSON POST.

These are integration possibilities, not claims of end-to-end verification with those products. Automated delivery tests use an ONVIF simulator and local HTTP recorders; real destination effects remain unverified.

## Documentation

Images for `linux/amd64` and `linux/arm64` are published to both [Docker Hub](https://hub.docker.com/r/dombrovskyi/onvif-webhook-bridge) (`dombrovskyi/onvif-webhook-bridge`) and [GitHub Container Registry (GHCR)](https://github.com/dombrovsky/onvif-webhook-bridge/pkgs/container/onvif-webhook-bridge) (`ghcr.io/dombrovsky/onvif-webhook-bridge`). Pin a version tag (without `v`) or, for content immutability, an image digest. Prereleases never update `latest`; stable releases do. The Overview page shows the running executable's version.

- [Deployment, upgrades, backup and troubleshooting](docs/DEPLOYMENT.md)
- [Browser configuration, Event Explorer and rules](docs/BROWSER_ADMINISTRATION.md)
- [Administrator setup, password reset and API](docs/ADMINISTRATION.md)
- [Data storage and permissions](docs/DATA_STORAGE.md)
- [Security and reporting vulnerabilities](SECURITY.md)
- [Development, tests and diagnostic probe](docs/DEVELOPMENT.md)

## Compatibility and limitations

- Cameras must expose a reachable ONVIF event service with PullPoint support. Manufacturer and firmware differences affect topics, fields, authentication and subscriptions; ONVIF branding alone does not establish compatibility. There is no automatic camera discovery.
- Validate activation/reset cycles, stable event identities, outage/reboot recovery and destination effects on your own devices. Simulator/recorder tests and hosted CI do not establish device interoperability. Use the [diagnostic probe](docs/DEVELOPMENT.md#diagnostic-probe) to inspect actual notifications.
- Docker targets are `linux/amd64` and `linux/arm64`. The [CI workflow](.github/workflows/ci.yml) tests both on native runners; operator hardware compatibility still needs verification. ARMv7 is excluded and Linux 32-bit x86 is unsupported.
- Event history and delivery results are bounded and in memory. Restart clears history, sessions, transition state, cooldowns and queued work. Events missed during disconnection are not replayed; delivery is not durable or exactly once.
- This is an event bridge. It does not stream or record video, proxy RTSP, or provide MQTT integration.

## Contributing and support

Report bugs and request features through [GitHub Issues](https://github.com/dombrovsky/onvif-webhook-bridge/issues). Include the revision, architecture and sanitized reproduction steps; keep credentials, device identities, private addresses and event payloads out of reports. Use the [security policy](SECURITY.md) for vulnerabilities.

For contributions, start with the [developer guide](docs/DEVELOPMENT.md) and [repository instructions](AGENTS.md). Keep changes focused and discuss substantial additions in an issue first. The project is free and open source under the [MIT License](LICENSE).

## Optional sponsorship

If you find the project useful, you can [sponsor its maintainer](https://github.com/sponsors/dombrovsky). Sponsorship is entirely optional; all features are available without payment.
