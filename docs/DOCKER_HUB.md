# ONVIF Webhook Bridge

Turn ONVIF IP camera events into HTTP webhooks. One self-hosted NativeAOT application includes browser administration, independent camera subscriptions and SQLite; no cloud account or external database is required.

## Install with Docker

Linux **AMD64** and **ARM64** images are available. Version **0.1.0-rc.3** is a prerelease. No Compose, clone or local build is needed.

Create a private file-backed password in a Docker volume (hidden prompt, 12–4096 characters). No host sudo or UID changes are needed:

```sh
docker run --rm -it --mount type=volume,src=onvif-bridge-bootstrap,dst=/data --entrypoint /bin/bash dombrovskyi/onvif-webhook-bridge:0.1.0-rc.3 -c 'umask 077; IFS= read -r -s -p "Administrator password: " password && printf "\n" && test "${#password}" -ge 12 && test "${#password}" -le 4096 && printf "%s" "$password" > /data/administrator-password'
```

Start the application:

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

Open **http://localhost:8080 on the Docker host**, sign in as `admin` with your password, and add a camera. For a remote server/NAS, use an SSH tunnel or validated HTTPS setup; the default host binding is loopback. A [Compose alternative and full configuration reference](https://github.com/dombrovsky/onvif-webhook-bridge/blob/main/docs/DEPLOYMENT.md#docker-compose-alternative) are available.

The service runs as UID/GID **1654:1654** with a read-only root filesystem. Docker initializes the private named volumes with the correct ownership. `onvif-bridge-data` persists configuration at `/data`; the separate `onvif-bridge-bootstrap` volume holds the password file. Bootstrap applies only to an empty administrator record; changing the file later does not change login. No plaintext password environment variable is required. Bind mounts require explicit ownership and private permissions; stored camera/webhook secrets and backups are recoverable plaintext.

## Versions and upgrades

Pin a version tag (without `v`) or a digest. GHCR alternative: `ghcr.io/dombrovsky/onvif-webhook-bridge:0.1.0-rc.3`. Replace the image in both commands to select another registry/version. After the first stable release, `dombrovskyi/onvif-webhook-bridge:latest` selects the current stable image. `latest` is not currently published; prereleases never create or update it.

Stop and securely back up the complete data volume before upgrading. Pull the chosen image, remove only the stopped container, then repeat the startup command with the new image and the **same volume names**. Verify health, login, Overview version and saved configuration. [Full Docker run/Compose upgrade, backup and recovery procedures](https://github.com/dombrovsky/onvif-webhook-bridge/blob/main/docs/DEPLOYMENT.md#storage-backup-upgrades-and-recovery).

Cameras need reachable ONVIF PullPoint services. Topics and fields vary by firmware; real-device interoperability and destination effects require verification. History, sessions and pending deliveries are in memory; missed events are not replayed. No video recording, RTSP proxy or MQTT is provided.

[GitHub and full documentation](https://github.com/dombrovsky/onvif-webhook-bridge) · [Browser guide](https://github.com/dombrovsky/onvif-webhook-bridge/blob/main/docs/BROWSER_ADMINISTRATION.md) · [Security](https://github.com/dombrovsky/onvif-webhook-bridge/blob/main/SECURITY.md)
