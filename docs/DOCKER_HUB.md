# ONVIF Webhook Bridge

Turn ONVIF IP camera events into HTTP webhooks. One self-hosted NativeAOT application includes browser administration, independent camera subscriptions and SQLite; no cloud account or external database is required.

## Install with Docker Compose

Linux **AMD64** and **ARM64** images are available. Version **0.1.0-rc.3** is a prerelease. Docker Compose v2 or later is recommended. No clone or local build is needed.

```sh
mkdir onvif-webhook-bridge
cd onvif-webhook-bridge
curl -fsSLo docker-compose.yml https://raw.githubusercontent.com/dombrovsky/onvif-webhook-bridge/main/docker-compose.yml
```

Create a private file-backed password in a Docker volume (hidden prompt, 12-4096 characters). No host sudo or UID changes are needed:

```sh
docker run --rm -it --mount type=volume,src=onvif-bridge-bootstrap,dst=/data --entrypoint /bin/bash dombrovskyi/onvif-webhook-bridge:0.1.0-rc.3 -c 'umask 077; read -r -s -p "Administrator password: " password; printf "\n"; test "${#password}" -ge 12 && test "${#password}" -le 4096 && printf "%s" "$password" > /data/administrator-password'
docker compose pull
docker compose up -d --no-build
docker compose ps
```

Open **http://localhost:8080 on the Docker host**, sign in as `admin` with your password, add a camera, and inspect Event Explorer before enabling a reviewed rule. For a remote server/NAS, use an SSH tunnel or validated HTTPS setup; the default host binding is loopback. See the [full installation and configuration reference](https://github.com/dombrovsky/onvif-webhook-bridge/blob/main/docs/DEPLOYMENT.md) for the complete YAML and NAS instructions.

The service runs as UID/GID **1654:1654** with a read-only root filesystem. Docker initializes the private named volumes with the correct ownership. `bridge-data` persists configuration at `/data`; the separate external `onvif-bridge-bootstrap` volume holds the password file. Bootstrap applies only to an empty administrator record; changing the file later does not change login. No plaintext password environment variable is required. Bind mounts require explicit ownership and private permissions; stored camera/webhook secrets and backups are recoverable plaintext.

## Versions and upgrades

Pin a version tag (without `v`) or a digest. GHCR alternative: `ghcr.io/dombrovsky/onvif-webhook-bridge:0.1.0-rc.3`. Set `ONVIF_IMAGE` in local `.env` to select another image. After the first stable release, `ONVIF_IMAGE=dombrovskyi/onvif-webhook-bridge:latest` selects the current stable image. `latest` is not currently published; prereleases never create or update it.

Stop and securely back up the complete data volume before upgrading. Keep the same Compose project, YAML and volumes; change `ONVIF_IMAGE`, then run `docker compose pull` and `docker compose up -d --no-build`. Verify health, login, Overview version and saved configuration. Never use `down --volumes` for an upgrade. [Backup, reset and rollback procedures](https://github.com/dombrovsky/onvif-webhook-bridge/blob/main/docs/DEPLOYMENT.md#storage-backup-upgrades-and-recovery).

Cameras need reachable ONVIF PullPoint services. Topics and fields vary by firmware; real-device interoperability and destination effects require verification. History, sessions and pending deliveries are in memory; missed events are not replayed. No video recording, RTSP proxy or MQTT is provided.

[GitHub and full documentation](https://github.com/dombrovsky/onvif-webhook-bridge) · [Browser guide](https://github.com/dombrovsky/onvif-webhook-bridge/blob/main/docs/BROWSER_ADMINISTRATION.md) · [Security](https://github.com/dombrovsky/onvif-webhook-bridge/blob/main/SECURITY.md)
