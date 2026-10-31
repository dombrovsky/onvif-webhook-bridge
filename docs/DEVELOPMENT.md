# Development and diagnostic tools

For installation and first use, start with the [project README](../README.md). This guide covers the current architecture, test commands and standalone probe. Inspect the selected revision's [CI results](../.github/workflows/ci.yml) for current verification.

## Structure and build

```text
OnvifWebhookBridge.slnx
Directory.Build.props / Directory.Build.targets / .editorconfig
Dockerfile / .dockerignore / docker-compose.yml / .env.example
src/
  OnvifWebhookBridge/              Application and shared ONVIF adapter
  OnvifProbe/                      Standalone diagnostic console
tests/
  OnvifWebhookBridge.Tests/        xUnit unit and Testcontainers integration tests
  OnvifWebhookBridge.NativeSmoke/  Test-only Linux NativeAOT protocol runner
  Verify-Phase7Native.ps1          Native security/API/storage/protocol checks
  Build-OnvifSimulator.ps1         Pinned upstream simulator image build
  OnvifProbe.Smoke.ps1             Synthetic console regression
```

```powershell
dotnet format whitespace OnvifWebhookBridge.slnx
dotnet format whitespace OnvifWebhookBridge.slnx --verify-no-changes
dotnet build OnvifWebhookBridge.slnx -c Release
dotnet test OnvifWebhookBridge.slnx -c Release --no-build --filter Category=Unit
```

Docker integration tests require a running Linux Docker engine. Build the simulator once with PowerShell 7, then run the tests (a missing Docker engine/image fails the integration test):

```powershell
./tests/Build-OnvifSimulator.ps1
dotnet test OnvifWebhookBridge.slnx -c Release --no-build
```

The script builds [onvifsim's upstream Dockerfile](https://github.com/mrtian2016/onvifsim/blob/8f86706c2ba768006e85b38f6bc1c760d36f1bba/packaging/docker/Dockerfile) at the pinned commit. Testcontainers starts a disposable simulator, uses random host ports, loads a generic synthetic camera scenario with fake credentials and mapped advertised addresses, subscribes, renews, triggers true/false notifications through its control API, pulls both states and unsubscribes. No physical camera or destination is contacted. Containers are disposed automatically; the image remains cached. Add `--no-cache --pull` to the script's Docker build command when intentionally refreshing the upstream base/OS packages, then rerun the tests.

Run `./tests/Verify-Phase7Native.ps1` for Linux NativeAOT publication and runtime checks of protocol parsing, matching, HTTP recorder delivery, overload/redaction, multiple-camera recovery, SQLite transactions/migrations/permissions, authentication and persisted restart. `-StorageOnly` omits onvifsim. The runner stages an explicit source allowlist under ignored `.local/` and removes its disposable containers; images remain cached. It accepts `-Platform linux/amd64` or `-Platform linux/arm64`. Native runners are preferred; on an amd64 host, `-Platform linux/arm64 -CrossCompile` uses emulation and must be reported as such. The script names are retained for compatibility with existing CI.

Run `./tests/Verify-Phase7Browser.ps1 -PlaywrightModule <installed-playwright-module>` after native verification for the browser walkthrough against actual APIs, onvifsim and a local recorder. Install Playwright and its browser as shown in [CI](../.github/workflows/ci.yml); use `ONVIF_BROWSER_CHANNEL=chromium` when using its bundled Chromium. Node/Playwright are test tools only, not application dependencies.

For production packaging, Compose, CI and registry release instructions, see [deployment](DEPLOYMENT.md). For the public release checklist and current verification status, see [release readiness](DEPLOYMENT.md#public-release-readiness).

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

## Testing the probe and a device

Use the pinned [SharpOnvif source revision](https://github.com/jimm98y/SharpOnvif/tree/a56b8c5f47dc3ce9ba832eb8ebcb1a7eafa5bb71) when reviewing its API. After a Debug build, run `./tests/OnvifProbe.Smoke.ps1` for synthetic notification/renewal/fault regressions. The harness force-terminates its child after assertions; it does not test graceful Ctrl+C or real-device behavior.

For device testing, use a machine with authorized network access and keep configuration/captures private:

1. Set the actual device-service URL and optional credentials locally, then run with `--raw`.
2. Check that service discovery and PullPoint subscription succeed. If discovery fails, verify endpoint, credentials, firewall, and device clock; authentication can depend on clock accuracy. Exception categories are printed without potentially sensitive server messages.
3. Generate each available detection/event type, then let it clear. Observe the actual topic, field names, values, camera timestamp and property operation. Repeat and confirm repeated notifications and false/reset states remain visible. A device may not emit a reset; record what actually occurs.
4. Leave it running across several lease periods and check renewal. With a coordinated network interruption, check reconnect/resubscribe. Do not modify camera settings to perform these tests.
5. Press Ctrl+C and check that shutdown completes. Events observed in another client are not proof that this tool is interoperable; compare behaviour directly.

The probe shares only the renewal-adapter source with the application and has no web-host dependency. Keep any private launch profile under `src/OnvifProbe/Properties/` ignored.

## Dispatch and event contracts

TaskFlow 1.1.0-rc.9 owns keyed scheduling, capacity and lifetime; retain its published package and consult its version-matched source. The pipeline takes a synchronous admission callback: production binds the singleton dispatcher; configuration-only validation supplies an explicit rejecting callback without allocating a flow. Admission updates cooldown only on acceptance and takes immutable configuration revisions. The dispatcher keeps application HTTP policy and completion status mapping. Final disposal settles the original flow before dependencies are disposed; a cancelled hosted stop only ends the host's wait. See [operation/shutdown](DEPLOYMENT.md#first-start) and [matching/delivery](BROWSER_ADMINISTRATION.md#matching-and-delivery).

Tests must wait for TaskFlow completion/quiescence when reusing capacity or pausing execution. Terminal logging occurs before a delegate returns. The dispatch fixture uses same-key, no-HTTP barriers to wait until prior work has quiesced. Transport, cancellation and fake-clock timer gates exercise ordering without sleeps.

Normalization preserves the library-exposed topic/namespace context, duplicate Source/Data/Key fields, ElementItems, property operations and extensions. Retention is bounded: 64 KiB per event, 16 KiB per string and 256 fields across Source/Data/Key; history is at most 100 events / 1 MiB globally. Truncated events remain inspectable, cannot match exact rules and reset uncertain transition baselines. Parsed DOM XML is not original SOAP bytes; content lost by library deserialization cannot be reconstructed. These bounds and preservation rules are enforced by normalization/matching tests.
