# ONVIF Webhook Bridge

Incremental .NET 10 NativeAOT bridge, with a standalone ONVIF diagnostic probe, phase 2 normalization/history/matching and phase 3 single-camera HTTP forwarding. The application exposes only `/health` and starts with no camera or rules configured. In-process callers can supply private in-memory configuration before hosted services start; there are no event, configuration or manual-test HTTP routes. Multiple-camera supervision, persistence and administration remain future work. See [HTTP verification and component use](docs/HTTP_VERIFICATION.md).

## Structure and build

```text
OnvifWebhookBridge.slnx
Directory.Build.props / Directory.Build.targets / .editorconfig
Dockerfile / .dockerignore
src/
  OnvifWebhookBridge/              Application and shared ONVIF adapter
  OnvifProbe/                      Standalone diagnostic console
tests/
  OnvifWebhookBridge.Tests/        xUnit unit and Testcontainers integration tests
  OnvifWebhookBridge.NativeSmoke/  Test-only Linux NativeAOT protocol runner
  Verify-Phase2Native.ps1          Native build/run against disposable onvifsim
  Build-OnvifSimulator.ps1         Pinned upstream simulator image build
  OnvifProbe.Smoke.ps1             Synthetic console regression
```

```powershell
dotnet build OnvifWebhookBridge.slnx
dotnet test OnvifWebhookBridge.slnx --no-build --filter Category=Unit
```

Docker integration tests require a running Linux Docker engine. Build the simulator once with PowerShell 7, then run the tests (a missing Docker engine/image fails the integration test):

```powershell
./tests/Build-OnvifSimulator.ps1
dotnet test OnvifWebhookBridge.slnx --no-build
```

The script builds [onvifsim's upstream Dockerfile](https://github.com/mrtian2016/onvifsim/blob/8f86706c2ba768006e85b38f6bc1c760d36f1bba/packaging/docker/Dockerfile) at the pinned commit. Testcontainers starts a disposable simulator, uses random host ports, loads a generic synthetic camera scenario with fake credentials and mapped advertised addresses, subscribes, renews, triggers true/false notifications through its control API, pulls both states and unsubscribes. No physical camera or destination is contacted. Containers are disposed automatically; the image remains cached. Add `--no-cache --pull` to the script's Docker build command when intentionally refreshing the upstream base/OS packages, then rerun the tests.

Run `./tests/Verify-Phase2Native.ps1` for the Linux x64 NativeAOT checks of both phases 2 and 3: simulator protocol, preservation/matching and the actual single-camera worker to a local GET/POST recorder, including overflow, failure isolation, manual testing, redirects and redaction. The historical script name is retained. It builds a test-only image from an explicit source allowlist staged under ignored `.local/`, runs against a disposable onvifsim container and removes the containers. It requires Linux amd64 Docker; it does not establish arm64 or physical-device interoperability. See [phase 2 evidence and component contracts](docs/PHASE2_VERIFICATION.md), including truncation and transition-identity limits. Real activation/reset cycles remain pending before using a real transition mapping.

Build the application image from the repository root:

```powershell
docker build -t onvif-webhook-bridge:scaffold .
docker run --rm -p 8080:8080 onvif-webhook-bridge:scaffold
```

This is a health-only development shell on port 8080, with no administrator or event routes. The Dockerfile publishes NativeAOT for the builder's Linux architecture and runs as the image's non-root account. The base image manifests are digest-pinned; only linux/amd64 has been checked for this scaffold. Production deployment, Compose, persistence permissions, health probes and multi-platform verification remain later work. See [scaffold verification](docs/SCAFFOLD_VERIFICATION.md).

## Diagnostic probe

A small exploratory .NET 10 console tool using SharpOnvifClient 0.11.2. It discovers the event service from a configured device-service endpoint, subscribes without topic filtering, and prints every returned notification. It does not change camera configuration or forward events anywhere.

## Run

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

## Output and behaviour

Each event has a local receipt timestamp with milliseconds and timezone, the full topic text/dialect, and the library-exposed message tree. Camera timestamps such as `UtcTime`, property operations, Source/Data/Key fields, duplicate names, boolean text, attributes, and structured/unknown elements are printed without an assumed schema. There is no topic filter, deduplication, or suppression of false states. `--raw` additionally prints the message XML exposed by the library, not a complete original SOAP envelope or byte-for-byte network capture.

Status messages report connecting, subscribing, renewing, reconnecting, and stopping. Endpoint URLs and credentials are omitted from status/error messages. Notification payloads themselves may contain private details: keep console output/captures private and sanitize before sharing. No files are written by the probe. Do not commit personal addresses, device identities, real configuration, or captures; `.local/`, `.env`, and `*.local.*` are ignored.

The probe requests a 120-second lease and pulls up to 100 notifications with at most a 20-second long poll. It uses camera-relative lease times for renewal through the generated SubscriptionManagerClient. Version 0.11.2 does not automatically include WS-Addressing headers on that client's renewal request. A small adapter adds Action, a unique MessageID, To, and anonymous ReplyTo through the supported header hook, while preserving the library's authentication. Without MessageID, subscription managers requiring it can return ActionFailed on every renewal. On faults, expired leases, or disconnects the probe disposes the client and attempts a fresh connection/subscription after three seconds. A small delay prevents devices returning immediately from causing a busy loop. Reconnection can lose events while disconnected; this diagnostic does not provide durable delivery.

The SimpleOnvifClient convenience calls do not expose CancellationToken in this version. Cancellable waits plus client disposal bound shutdown; renewal uses the generated API's cancellation token. Subscription reference parameters are not forwarded by the convenience methods: a warning identifies devices that return them. Such devices may require a lower-level client. Unknown XML already discarded by library deserialization cannot be recovered here. Subscription/producer reference addresses are intentionally omitted because they can contain private URLs/tokens. No built-in motion helpers are used.

## Verification and local-network testing

Source/API review used the [package's upstream revision](https://github.com/jimm98y/SharpOnvif/tree/a56b8c5f47dc3ce9ba832eb8ebcb1a7eafa5bb71), including SimpleOnvifClient, the console example, and generated event contracts. The `net10.0` package was restored and the application built with .NET SDK 10.0.400 with zero warnings/errors. The user also reports successful NativeAOT and trimming verification with the library; the exact target and publish/runtime checks were not supplied. Linux/container and per-architecture verification from the longer product plan remain separate checks.

Verification also passed against a simulated SOAP service: six notifications (including repeated true and false states), duplicate fields, structured/unknown XML, unique WS-Addressing renewal headers and resubscription after a fault. Help and invalid configuration checks passed. A console Ctrl+C check during connection/retry reached the `Stopped` status. Run the synthetic regression check from the repository root with PowerShell 7 after a Debug build:

```powershell
./tests/OnvifProbe.Smoke.ps1
```

The test uses a synthetic localhost SOAP listener and bypasses launch profiles; it does not access a camera. It force-terminates its child after assertions, so its result is not a graceful shutdown check.

An initial reachability check failed. A later real-device run using private local configuration successfully connected and received initialization notifications. The old renewal fault was reproduced and traced to missing WS-Addressing headers. With the fix, the probe completed at least 25 successful renewals over more than 40 minutes without recurrence, and Ctrl+C successfully unsubscribed and stopped it. Evidence is recorded in [the observation record](docs/ONVIF_OBSERVATIONS.md). This does not establish every event type's activation/reset lifecycle, reboot recovery, or interoperability with other devices. Run the probe on a machine on the same reachable LAN as the device:

1. Set the actual device-service URL and optional credentials locally, then run with `--raw`.
2. Check that service discovery and PullPoint subscription succeed. If discovery fails, verify endpoint, credentials, firewall, and device clock; authentication can depend on clock accuracy. Exception categories are printed without potentially sensitive server messages.
3. Generate each available detection/event type, then let it clear. Observe the actual topic, field names, values, camera timestamp and property operation. Repeat and confirm repeated notifications and false/reset states remain visible. A device may not emit a reset; record what actually occurs.
4. Leave it running across several lease periods and check renewal. With a coordinated network interruption, check reconnect/resubscribe. Do not modify camera settings to perform these tests.
5. Press Ctrl+C and check that shutdown completes. Events observed in another client are not proof that this tool is interoperable; compare behaviour directly.

The probe remains a small standalone console application. It shares only the renewal-adapter source with the application, without depending on the web host. Existing ignored launch profiles in the old `tools` directory are not moved; recreate a private profile under `src/OnvifProbe/Properties/` if needed. The planning documents describe the eventual product separately.
